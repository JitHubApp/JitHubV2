use std::error::Error;
use std::fs;
use std::path::PathBuf;

const WIDTH: u32 = 256;
const HEIGHT: u32 = 256;

fn main() -> Result<(), Box<dyn Error>> {
    let mut rgba = Vec::with_capacity(WIDTH as usize * HEIGHT as usize * 4);
    let mut state = 0x6d2b_79f5u32;
    for _ in 0..WIDTH * HEIGHT {
        for _ in 0..3 {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            rgba.push((state >> 24) as u8);
        }
        rgba.push(255);
    }

    let encoded = webp::Encoder::from_rgba(&rgba, WIDTH, HEIGHT)
        .encode(75.0)
        .to_vec();
    if encoded.get(0..4) != Some(b"RIFF") || encoded.get(12..16) != Some(b"VP8 ") {
        return Err("encoder did not produce an opaque lossy VP8 WebP".into());
    }

    let output_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures");
    fs::create_dir_all(&output_dir)?;
    let output = output_dir.join("opaque-vp8-256.webp");
    fs::write(&output, &encoded)?;
    println!("{} ({} bytes)", output.display(), encoded.len());
    Ok(())
}
