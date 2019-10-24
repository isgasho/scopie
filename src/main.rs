#[macro_use]
extern crate lazy_static;
extern crate regex;
extern crate sdl2;
extern crate serialport;
extern crate time;

mod camera;
mod camera_feed;
mod display;
mod dms;
mod init_script;
mod mount;
mod qhycamera;
mod process;

use camera::Camera;
use camera::CameraInfo;
use camera_feed::CameraFeed;
use init_script::InitScript;
use mount::Mount;
use std::error::Error;
use std::io::stdin;
use std::io::stdout;
use std::io::BufRead;
use std::io::Write;
use std::sync::Arc;
use std::time::Instant;

type Result<T> = std::result::Result<T, Box<dyn Error>>;

pub struct Image<T> {
    data: Vec<T>,
    width: usize,
    height: usize,
}

impl<T> Image<T> {
    pub fn new(data: Vec<T>, width: usize, height: usize) -> Self {
        Self {
            data,
            width,
            height,
        }
    }
}

fn repl_camera(command: &[&str], camera: &Arc<CameraFeed>) -> Result<bool> {
    let good_command = match command.first() {
        Some(&"help") if command.len() == 1 => {
            println!("info -- print variable information");
            println!("zoom -- zoom to center 100x100px");
            println!("cross -- overlay red cross in middle of image");
            println!("{{var_name}} -- print variable's value");
            println!("{{var_name}} {{value}} -- set variable to value");
            true
        }
        Some(&"init") if command.len() == 1 => {
            CameraFeed::init(&camera)?;
            true
        }
        Some(&"info") if command.len() == 1 => {
            for control in camera.camera().controls() {
                println!("{}", control);
            }
            true
        }
        Some(&"zoom") if command.len() == 1 => {
            // invert
            camera.image_adjust_options().lock().unwrap().zoom ^= true;
            true
        }
        Some(&"cross") if command.len() == 1 => {
            camera.image_adjust_options().lock().unwrap().cross ^= true;
            true
        }
        Some(cmd) if command.len() == 2 => {
            let mut ok = false;
            if let Ok(value) = command[1].parse() {
                for control in camera.camera().controls() {
                    if control.name().eq_ignore_ascii_case(cmd) {
                        control.set(value)?;
                        ok = true;
                        break;
                    }
                }
            }
            ok
        }
        Some(cmd) if command.len() == 1 => {
            let mut ok = false;
            for control in camera.camera().controls() {
                if control.name().eq_ignore_ascii_case(cmd) {
                    println!("{}", control);
                    ok = true;
                    break;
                }
            }
            ok
        }
        Some(_) => false,
        None => true,
    };
    Ok(good_command)
}

fn repl_mount(command: &[&str], mount: &mut Mount) -> Result<bool> {
    let good_command = match command.first() {
        Some(&"help") if command.len() == 1 => {
            println!("pos -- print position");
            println!("setpos {{ra}} {{dec}} -- overwrite position");
            println!("slew {{ra}} {{dec}} -- slew to position");
            println!("cancel -- cancel slew");
            println!("mode -- print tracking mode");
            println!("mode {{Off|AltAz|Equatorial|SiderealPec}} -- set tracking mode");
            println!("location -- print location");
            println!("location {{lat}} {{lon}} -- set location");
            println!("time -- print mount's time");
            println!("time now -- set mount time to present");
            println!("aligned -- print if mount is aligned");
            println!("ping -- ping telescope");
            true
        }
        Some(&"pos") if command.len() == 1 => {
            let (ra, dec) = mount.get_ra_dec()?;
            println!(
                "{} {}",
                dms::print_dms(ra, true),
                dms::print_dms(dec, false)
            );
            true
        }
        Some(&"setpos") if command.len() == 3 => {
            let ra = dms::parse_dms(command[1]);
            let dec = dms::parse_dms(command[2]);
            if let (Some(ra), Some(dec)) = (ra, dec) {
                mount.overwrite_ra_dec(ra, dec)?;
                println!("ok");
                true
            } else {
                false
            }
        }
        Some(&"slew") if command.len() == 3 => {
            let ra = dms::parse_dms(command[1]);
            let dec = dms::parse_dms(command[2]);
            if let (Some(ra), Some(dec)) = (ra, dec) {
                mount.slew_ra_dec(ra, dec)?;
                println!("ok");
                true
            } else {
                false
            }
        }
        Some(&"cancel") if command.len() == 1 => {
            mount.cancel_slew()?;
            println!("ok");
            true
        }
        Some(&"mode") if command.len() == 1 => {
            println!("{}", mount.tracking_mode()?);
            true
        }
        Some(&"mode") if command.len() == 2 => {
            match command[1].parse() {
                Ok(mode) => {
                    mount.set_tracking_mode(mode)?;
                    println!("ok");
                }
                Err(err) => println!("{}", err),
            }
            true
        }
        Some(&"location") if command.len() == 1 => {
            let (lat, lon) = mount.location()?;
            println!(
                "{} {}",
                dms::print_dms(lat, false),
                dms::print_dms(lon, false)
            );
            true
        }
        Some(&"location") if command.len() == 3 => {
            let lat = dms::parse_dms(command[1]);
            let lon = dms::parse_dms(command[2]);
            if let (Some(lat), Some(lon)) = (lat, lon) {
                mount.set_location(lat, lon)?;
                println!("ok");
                true
            } else {
                false
            }
        }
        Some(&"time") if command.len() == 1 => {
            println!("{}", mount.time()?);
            true
        }
        Some(&"time") if command.len() == 2 && command[1] == "now" => {
            mount.set_time(mount::MountTime::now())?;
            println!("ok");
            true
        }
        Some(&"aligned") if command.len() == 1 => {
            let aligned = mount.aligned()?;
            println!("{}", aligned);
            true
        }
        Some(&"ping") if command.len() == 1 => {
            let now = Instant::now();
            let ok = mount.echo('U' as u8)? == 'U' as u8;
            let duration = now.elapsed();
            let duration_seconds =
                duration.as_secs() as f32 + duration.subsec_nanos() as f32 * 1e-9;
            println!("{} seconds (ok={})", duration_seconds, ok);
            true
        }
        Some(_) => false,
        None => true,
    };
    Ok(good_command)
}

fn do_script(
    name: &str,
    camera: &mut Option<Arc<CameraFeed>>,
    mount: &mut Option<Mount>,
) -> Result<()> {
    let init = match InitScript::new("init") {
        Ok(x) => x,
        Err(err) => {
            println!("Couldn't open init script, not running: {}", err);
            return Ok(());
        }
    };
    let script = init.script(name);
    for line in script {
        println!("script> {}", line);
        repl_one(&line, camera, mount)?;
    }
    Ok(())
}

fn repl_one(
    line: &str,
    camera: &mut Option<Arc<CameraFeed>>,
    mount: &mut Option<Mount>,
) -> Result<bool> {
    let command = line.split(' ').collect::<Vec<_>>();
    if let Some(camera) = camera.as_ref() {
        return Ok(repl_camera(&command, camera)?);
    }
    if let Some(mount) = mount.as_mut() {
        return Ok(repl_mount(&command, mount)?);
    }
    let good_command = match command.first() {
        Some(&"list") if command.len() == 1 => {
            let num_cameras = Camera::num_cameras();
            for i in 0..num_cameras {
                let camera = CameraInfo::new(i)?;
                println!("cameras[{}] = {}", i, camera.name());
            }
            for port in Mount::list() {
                println!("serial: {}", port);
            }
            true
        }
        Some(&"camera") if command.len() == 2 => {
            if let Ok(value) = command[1].parse() {
                let num_cameras = Camera::num_cameras();
                if value < num_cameras {
                    *camera = Some(CameraFeed::run(value)?);
                    let name = camera.as_ref().unwrap().camera().name();
                    do_script(name, camera, mount)?;
                } else {
                    println!("Camera index out of range");
                }
                true
            } else {
                false
            }
        }
        Some(&"mount") if command.len() == 2 => {
            if let Ok(num) = command[1].parse::<usize>() {
                if let Some(path) = Mount::list().get(num) {
                    *mount = Some(Mount::new(path)?);
                    println!("Opened mount connection: {}", path);
                    do_script("mount", camera, mount)?;
                } else {
                    println!("Mount index out of range");
                }
            } else {
                *mount = Some(Mount::new(command[1])?);
                println!("Opened mount connection");
            };
            true
        }
        Some(_) => false,
        None => true,
    };
    Ok(good_command)
}

fn try_main() -> Result<()> {
    let mut camera = None;
    let mut mount = None;
    let stdin = stdin();
    print!("> ");
    stdout().flush()?;
    for line in stdin.lock().lines() {
        let line = line?;
        // maybe we should catch/print error here, instead of exiting
        let ok = repl_one(&line, &mut camera, &mut mount)?;
        if !ok {
            println!("Unknown command: {}", line);
        }
        print!("> ");
        stdout().flush()?;
    }
    Ok(())
}

fn main() {
    match try_main() {
        Ok(()) => (),
        Err(err) => println!("Error: {}", err),
    }
}
