use std::path::PathBuf;

// Import the capture module directly
use clowd_capture::{start, cli::ProgramArgs};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    // Create output paths in examples directory
    let examples_dir = PathBuf::from(".");
    let capture_path = examples_dir.join("example_capture.png");
    let result_path = examples_dir.join("example_capture.json");
    
    // Clean up any existing files
    if capture_path.exists() {
        std::fs::remove_file(&capture_path)?;
    }
    if result_path.exists() {
        std::fs::remove_file(&result_path)?;
    }
    
    println!("Starting Clowd screen capturer...");
    println!("Capture will be saved to: {}", capture_path.display());
    println!("Result will be saved to: {}", result_path.display());
    println!("\nControls:");
    println!("- Click and drag to select an area");
    println!("- Press ESC to cancel");
    println!("- Use the toolbar buttons to save, copy, or edit");
    
    // Create CLI args directly
    let args = ProgramArgs {
        capture_path: capture_path.to_string_lossy().to_string(),
        result_path: result_path.to_string_lossy().to_string(),
        accent_color: Some("0,125,180".to_string()), // Optional blue theme
        low_perf_mode: Some(false),
    };
    
    // Call the start function directly
    start(args);
    
    println!("\nCapture completed!");
    
    // Read and display the result
    if result_path.exists() {
        let result_content = std::fs::read_to_string(&result_path)?;
        println!("Result: {}", result_content);
        
        if capture_path.exists() {
            println!("Screenshot saved to: {}", capture_path.display());
        }
    } else {
        println!("No result file found - operation may have been cancelled");
    }
    
    Ok(())
}