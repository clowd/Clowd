import { invoke } from "@tauri-apps/api/core";

export async function getImageUri(filePath: string): Promise<string> {
    return await invoke("get_image_uri", { file_path: filePath });
}