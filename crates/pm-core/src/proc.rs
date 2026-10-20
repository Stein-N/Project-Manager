//! Child processes (git, Gradle, Claude, IntelliJ …): no console window on Windows, stdout and stderr merged into
//! one stream that is read line by line, and stopping a process together with everything it started.

use std::io::{self, BufRead, BufReader, Read};
use std::path::Path;
use std::process::{Child, Command, Stdio};

/// A command that does not open a console window (Windows) and gets its own process group (Unix), so
/// [`kill_tree`] can stop it with all its children.
pub fn command(program: impl AsRef<std::ffi::OsStr>) -> Command {
    let mut cmd = Command::new(program);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        cmd.process_group(0);
    }
    cmd
}

/// Starts `cmd` with stdout and stderr in one pipe (in the order they were written) and stdin closed.
pub fn spawn_merged(mut cmd: Command) -> io::Result<(Child, os_pipe::PipeReader)> {
    spawn_merged_with_stdin(cmd.stdin(Stdio::null()))
}

/// Like [`spawn_merged`], but stdin is whatever `cmd` configured (e.g. a pipe for a prompt).
pub fn spawn_merged_with_stdin(cmd: &mut Command) -> io::Result<(Child, os_pipe::PipeReader)> {
    let (reader, writer) = os_pipe::pipe()?;
    cmd.stdout(writer.try_clone()?).stderr(writer);
    let child = cmd.spawn();
    // the command still holds the write ends; the reader only sees the end once they are closed
    cmd.stdout(Stdio::null()).stderr(Stdio::null());
    Ok((child?, reader))
}

/// Reads `reader` line by line (lossy UTF-8, without the line break) until the process closed it.
pub fn read_lines(reader: impl Read, mut each: impl FnMut(&str)) {
    let mut reader = BufReader::new(reader);
    let mut buf = Vec::new();
    loop {
        buf.clear();
        match reader.read_until(b'\n', &mut buf) {
            Ok(0) | Err(_) => break,
            Ok(_) => {
                while matches!(buf.last(), Some(b'\n' | b'\r')) {
                    buf.pop();
                }
                each(&String::from_utf8_lossy(&buf));
            }
        }
    }
}

/// Stops a process started with [`command`] (by its id) and every process it started (e.g. the Minecraft client of a
/// Gradle task).
pub fn kill_tree(pid: u32) {
    #[cfg(unix)]
    unsafe {
        // the process group of `command` has the id of its first process
        libc::kill(-(pid as i32), libc::SIGKILL);
    }
    #[cfg(windows)]
    {
        let _ = command("taskkill").args(["/T", "/F", "/PID", &pid.to_string()]).stdout(Stdio::null())
            .stderr(Stdio::null()).status();
    }
}

/// Starts a program detached from this one (IntelliJ, a terminal, the file manager); its output is discarded.
pub fn launch(cmd: &mut Command, dir: Option<&Path>) -> io::Result<()> {
    if let Some(dir) = dir {
        cmd.current_dir(dir);
    }
    cmd.stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
    let mut child = cmd.spawn()?;
    // reap it in the background so it does not stay a zombie
    std::thread::spawn(move || {
        let _ = child.wait();
    });
    Ok(())
}
