use std::fs::File;
use std::io::BufWriter;
use std::path::PathBuf;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let destination = std::env::args_os()
        .nth(1)
        .map(PathBuf::from)
        .ok_or("pass the generated WebP destination path")?;
    let width = 2048usize;
    let height = 8193usize;
    let mut pixels = vec![0u8; width * height * 4];
    for y in 0..height {
        for x in 0..width {
            let offset = (y * width + x) * 4;
            pixels[offset] = ((x * 13 + y * 3) & 0xff) as u8;
            pixels[offset + 1] = ((x * 5 + y * 11) & 0xff) as u8;
            pixels[offset + 2] = (((x >> 5) ^ (y >> 4)) & 0xff) as u8;
            pixels[offset + 3] = 255;
        }
    }
    let writer = BufWriter::new(File::create(destination)?);
    image_webp::WebPEncoder::new(writer).encode(
        &pixels,
        width as u32,
        height as u32,
        image_webp::ColorType::Rgba8,
    )?;
    Ok(())
}
