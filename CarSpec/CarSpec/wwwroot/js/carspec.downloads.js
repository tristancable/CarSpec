export function downloadBytes(filename, contentType, base64Data) {
    try {
        const link = document.createElement('a');
        link.href = `data:${contentType};base64,${base64Data}`;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    } catch (err) {
        console.error("downloadBytes failed", err);
    }
}