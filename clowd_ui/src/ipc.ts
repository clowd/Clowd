import { invoke } from "@tauri-apps/api/core";

export async function getImageUri(filePath: string): Promise<string> {
    return await invoke("get_image_uri", { file_path: filePath });
}

export async function showCurrentWindow(): Promise<void> {
    await invoke("show_current_window");
}