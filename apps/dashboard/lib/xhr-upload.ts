"use client";

import { authHeaders } from "./auth-store";

/**
 * Upload a FormData payload via XMLHttpRequest so we get real upload progress
 * events (fetch() does not expose the upload-side stream in browsers).
 * Returns the parsed JSON response on 2xx; throws an Error with the body
 * otherwise.
 */
export async function xhrUpload<T = unknown>(
  url: string,
  formData: FormData,
  onProgress?: (loaded: number, total: number) => void,
): Promise<T> {
  const auth = await authHeaders();
  return new Promise<T>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", url);
    for (const [k, v] of Object.entries(auth)) {
      if (v) xhr.setRequestHeader(k, v);
    }
    xhr.responseType = "text";
    xhr.upload.addEventListener("progress", (event) => {
      if (event.lengthComputable && onProgress) {
        onProgress(event.loaded, event.total);
      }
    });
    xhr.addEventListener("load", () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(xhr.responseText ? (JSON.parse(xhr.responseText) as T) : (undefined as T));
        } catch (err) {
          reject(err instanceof Error ? err : new Error(String(err)));
        }
      } else {
        reject(new Error(xhr.responseText || `Request failed: ${xhr.status}`));
      }
    });
    xhr.addEventListener("error", () => reject(new Error("Network error during upload")));
    xhr.addEventListener("abort", () => reject(new Error("Upload aborted")));
    xhr.send(formData);
  });
}
