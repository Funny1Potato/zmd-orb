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
        .run(|app, event| {
            if let RunEvent::Exit = event {
                if let Some(mut c) = app.state::<Backend>().0.lock().unwrap().take() {
                    kill_backend(&mut c);
                }
            }
        });
}