#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::Write;
use std::path::Path;
#[cfg(not(debug_assertions))]
use std::path::PathBuf;
use std::process::{Child, Command};
#[cfg(not(debug_assertions))]
use std::process::Stdio;
use std::sync::Mutex;
use tauri::{Manager, RunEvent};

/// 采集端子进程（Python backend），退出时回收整棵进程树。
struct Backend(Mutex<Option<Child>>);

/// 前端调用：整体退出（触发 RunEvent::Exit 回收采集端）。
#[tauri::command]
fn quit_app(app: tauri::AppHandle) {
    app.exit(0);
}

/// 球的右键菜单/双击调用：显示任务管理器面板。
#[tauri::command]
fn open_panel(app: tauri::AppHandle) {
    if let Some(w) = app.get_webview_window("panel") {
        let _ = w.show();
        let _ = w.set_focus();
    }
}

/// 面板顶栏的「收起到球」按钮。
#[tauri::command]
fn close_panel(app: tauri::AppHandle) {
    if let Some(w) = app.get_webview_window("panel") {
        let _ = w.hide();
    }
}

#[tauri::command]
fn set_ball_visible(app: tauri::AppHandle, visible: bool) {
    if let Some(w) = app.get_webview_window("ball") {
        if visible {
            let _ = w.show();
        } else {
            let _ = w.hide();
        }
    }
}

/// 强杀 backend 进程树。PyInstaller onefile 会 fork 子进程承载实际逻辑，
/// 只 kill 直接子进程会留下孤儿 python 继续占 8910，故用 taskkill /T。
fn kill_backend(child: &mut Child) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        let _ = Command::new("taskkill")
            .args(["/PID", &child.id().to_string(), "/T", "/F"])
            .creation_flags(0x08000000) // CREATE_NO_WINDOW：不弹黑框
            .output();
    }
    let _ = child.kill();
    let _ = child.wait();
}

/// 诊断日志写到 %TEMP%/zmd_orb_backend_launch.log，便于后端起不来时定位
fn diag_log(s: &str) {
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(std::env::temp_dir().join("zmd_orb_backend_launch.log"))
    {
        let ts = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        let _ = writeln!(f, "[{}] {}", ts, s);
    }
}

/// 拉起采集端。
/// - release：backend.exe 由 Tauri 作为资源随安装包落地到 resources 目录，
///   运行时从资源目录直接 spawn（不自解包，避免杀软把"释放 exe/dll"误判为恶意）。
///   为兼容不同 Tauri 资源布局，依次尝试多个候选路径；找不到则写诊断日志并回落演示数据。
/// - debug：直接用本地 python 跑采集端源码。
fn spawn_backend(resource_dir: &Path) -> Option<Child> {
    #[cfg(not(debug_assertions))]
    {
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|p| p.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from("."));
        let candidates = [
            resource_dir.join("backend.exe"),
            resource_dir.join("dist/backend.exe"),
            exe_dir.join("backend.exe"),
            exe_dir.join("resources/backend.exe"),
            exe_dir.join("../resources/backend.exe"),
        ];
        diag_log(&format!(
            "resource_dir={:?} exe_dir={:?}",
            resource_dir, exe_dir
        ));
        for c in &candidates {
            let exists = c.exists();
            diag_log(&format!("candidate {:?} exists={}", c, exists));
            if !exists {
                continue;
            }
            let stdout = std::fs::File::create(std::env::temp_dir().join("zmd_orb_backend_stdout.log"))
                .map(Stdio::from)
                .unwrap_or(Stdio::null());
            let stderr = std::fs::File::create(std::env::temp_dir().join("zmd_orb_backend_stderr.log"))
                .map(Stdio::from)
                .unwrap_or(Stdio::null());
            let mut cmd = Command::new(c);
            cmd.arg("--no-gui").stdout(stdout).stderr(stderr);
            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                cmd.creation_flags(0x08000000); // CREATE_NO_WINDOW：不弹黑框
            }
            match cmd.spawn() {
                Ok(child) => {
                    diag_log(&format!("spawned backend from {:?} pid={}", c, child.id()));
                    return Some(child);
                }
                Err(e) => {
                    diag_log(&format!("spawn failed {:?}: {}", c, e));
                }
            }
        }
        diag_log("未找到任何 backend.exe 候选，前端将回落演示数据");
        None
    }
    #[cfg(debug_assertions)]
    {
        let _ = resource_dir; // debug 分支走本地 python 源码，不消费 resource_dir
        // 相对 CARGO_MANIFEST_DIR 定位采集端源码，避免把开发者本机绝对路径提交进仓库。
        // Python 解释器优先读 ZMD_ORB_PYTHON 环境变量，否则回退 PATH 中的 python。
        let manifest = env!("CARGO_MANIFEST_DIR");
        let script = Path::new(manifest).join("../../collector/speed_collector.py");
        let python = std::env::var("ZMD_ORB_PYTHON").unwrap_or_else(|_| "python".to_string());
        Command::new(python)
            .arg(script)
            .arg("--no-gui")
            .spawn()
            .ok()
    }
}

/// Tauri 的 `skipTaskbar` 在实机上没生效：球的扩展样式仍是 WS_EX_APPWINDOW、且没有
/// WS_EX_TOOLWINDOW，结果它既在任务栏、也在 Alt-Tab 里（2026-09-26 装机实测）。
/// 这里自己改窗口扩展样式：加 WS_EX_TOOLWINDOW、去掉 WS_EX_APPWINDOW，
/// 再用 SWP_FRAMECHANGED 让外壳重新评估（不会闪一下）。
#[cfg(windows)]
fn hide_from_taskbar(window: &tauri::WebviewWindow) {
    use std::ffi::c_void;
    use std::ptr::null_mut;

    extern "system" {
        fn GetWindowLongW(hwnd: *mut c_void, index: i32) -> i32;
        fn SetWindowLongW(hwnd: *mut c_void, index: i32, value: i32) -> i32;
        fn SetWindowPos(hwnd: *mut c_void, after: *mut c_void, x: i32, y: i32,
                        cx: i32, cy: i32, flags: u32) -> i32;
    }

    const GWL_EXSTYLE: i32 = -20;
    const WS_EX_TOOLWINDOW: i32 = 0x0000_0080;
    const WS_EX_APPWINDOW: i32 = 0x0004_0000;
    const SWP_NOSIZE: u32 = 0x0001;
    const SWP_NOMOVE: u32 = 0x0002;
    const SWP_NOZORDER: u32 = 0x0004;
    const SWP_NOACTIVATE: u32 = 0x0010;
    const SWP_FRAMECHANGED: u32 = 0x0020;

    match window.hwnd() {
        Ok(h) => unsafe {
            let hwnd = h.0 as *mut c_void;
            let old = GetWindowLongW(hwnd, GWL_EXSTYLE);
            let new = (old | WS_EX_TOOLWINDOW) & !WS_EX_APPWINDOW;
            SetWindowLongW(hwnd, GWL_EXSTYLE, new);
            SetWindowPos(hwnd, null_mut(), 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            diag_log(&format!("hide_from_taskbar: exstyle 0x{:08X} -> 0x{:08X}",
                              old as u32, new as u32));
        },
        Err(e) => diag_log(&format!("hide_from_taskbar: 取不到 hwnd: {e}")),
    }
}

/// 把窗口的命中区裁成一个内切圆（方窗口的内切椭圆就是圆）。
/// 无边框透明窗口本身是矩形的，四角那些"看着是空的"区域照样会吃掉点击，
/// 让下面的窗口点不动；裁掉之后四角就点击穿透了。球的可见内容（含描边）最大半径
/// 约 76.6/80，落在这个圆内，所以不会被裁到。
#[cfg(windows)]
fn clip_to_circle(window: &tauri::WebviewWindow) {
    use std::ffi::c_void;

    #[repr(C)]
    struct RECT {
        left: i32,
        top: i32,
        right: i32,
        bottom: i32,
    }

    extern "system" {
        fn GetClientRect(hwnd: *mut c_void, rect: *mut RECT) -> i32;
        fn CreateEllipticRgn(x1: i32, y1: i32, x2: i32, y2: i32) -> *mut c_void;
        fn SetWindowRgn(hwnd: *mut c_void, rgn: *mut c_void, redraw: i32) -> i32;
        fn DeleteObject(obj: *mut c_void) -> i32;
    }

    match window.hwnd() {
        Ok(h) => unsafe {
            let hwnd = h.0 as *mut c_void;
            let mut rc = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            if GetClientRect(hwnd, &mut rc) == 0 {
                diag_log("clip_to_circle: GetClientRect 失败");
                return;
            }
            let (w, h) = (rc.right - rc.left, rc.bottom - rc.top);
            if w <= 0 || h <= 0 {
                return;
            }
            let rgn = CreateEllipticRgn(0, 0, w, h);
            if rgn.is_null() {
                diag_log("clip_to_circle: CreateEllipticRgn 失败");
                return;
            }
            if SetWindowRgn(hwnd, rgn, 1) == 0 {
                // 失败时区域仍归调用方所有，得自己释放（成功则交给系统，不能删）
                DeleteObject(rgn);
                diag_log("clip_to_circle: SetWindowRgn 失败");
            } else {
                diag_log(&format!("clip_to_circle: 已裁剪为 %dx%d 内切圆", w, h));
            }
        },
        Err(e) => diag_log(&format!("clip_to_circle: 取不到 hwnd: {e}")),
    }
}

fn main() {
    tauri::Builder::default()
        .manage(Backend(Mutex::new(None)))
        .setup(|app| {
            let resource_dir = app
                .path()
                .resource_dir()
                .expect("[壳] 无法解析资源目录（安装包可能损坏）");
            let child = spawn_backend(&resource_dir);
            if child.is_none() {
                eprintln!("[壳] 未能启动采集端 backend（页面将回落演示数据）");
            }
            *app.state::<Backend>().0.lock().unwrap() = child;

            // 球不进任务栏/Alt-Tab（Tauri 的 skipTaskbar 在 Windows 上没起作用，见函数注释）
            #[cfg(windows)]
            match app.get_webview_window("ball") {
                Some(ball) => {
                    hide_from_taskbar(&ball);
                    clip_to_circle(&ball);
                }
                None => diag_log("setup: 没找到 ball 窗口，跳过任务栏隐藏与圆形裁剪"),
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            quit_app,
            open_panel,
            close_panel,
            set_ball_visible
        ])
        .build(tauri::generate_context!())
        .expect("error building app")
        .run(|app, event| match event {
            RunEvent::Exit => {
                if let Some(mut c) = app.state::<Backend>().0.lock().unwrap().take() {
                    kill_backend(&mut c);
                }
            }
            // 拖到不同缩放的显示器时窗口尺寸会变，圆形裁剪区域要跟着重算
            #[cfg(windows)]
            RunEvent::WindowEvent { label, event } => {
                if label == "ball" && matches!(event, tauri::WindowEvent::Resized(_)) {
                    if let Some(ball) = app.get_webview_window("ball") {
                        clip_to_circle(&ball);
                    }
                }
            }
            _ => {}
        });
}