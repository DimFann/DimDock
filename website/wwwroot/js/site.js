function initializeMarkdownImageUploads({ form, markdownEditor, input, status, scale, uploadButton, dropzone, trackUploads = false }) {
    let uploading = false;

    form.addEventListener("submit", (event) => {
        if (uploading) {
            event.preventDefault();
            event.stopImmediatePropagation();
            status.textContent = "Wait for the image upload to finish before saving.";
        }
    }, true);

    async function uploadImages(files) {
        if (uploading || files.length === 0) {
            return;
        }

        uploading = true;
        uploadButton.disabled = true;
        const submitButtons = Array.from(form.querySelectorAll('button[type="submit"]'));
        submitButtons.forEach((button) => button.disabled = true);
        try {
            for (const file of files) {
                status.textContent = `Uploading ${file.name}...`;
                const formData = new FormData(form);
                formData.append("image", file);
                const url = new URL(window.location.href);
                url.searchParams.set("handler", "UploadImage");

                try {
                    const response = await fetch(url, {
                        method: "POST",
                        body: formData,
                        headers: { "X-Requested-With": "XMLHttpRequest" }
                    });
                    if (!response.ok) {
                        throw new Error(await response.text() || "Image upload failed.");
                    }

                    const result = await response.json();
                    if (trackUploads) {
                        const uploadedUrl = document.createElement("input");
                        uploadedUrl.type = "hidden";
                        uploadedUrl.name = "uploadedImageUrls";
                        uploadedUrl.value = result.url;
                        form.append(uploadedUrl);
                    }
                    const altText = result.fileName
                        .replaceAll("\\", "\\\\")
                        .replaceAll("]", "\\]")
                        .replace(/[\r\n]/g, " ");
                    const imageMarkdown = `![${altText}](${result.url} "scale=${scale.value}%")`;
                    const start = markdownEditor.selectionStart;
                    const end = markdownEditor.selectionEnd;
                    const before = start > 0 && markdownEditor.value[start - 1] !== "\n" ? "\n" : "";
                    const after = end < markdownEditor.value.length && markdownEditor.value[end] !== "\n" ? "\n" : "";
                    markdownEditor.setRangeText(`${before}${imageMarkdown}${after}`, start, end, "end");
                    markdownEditor.focus();
                    status.textContent = `Uploaded ${file.name}.`;
                } catch (requestError) {
                    status.textContent = requestError.message;
                    break;
                }
            }
        } finally {
            uploading = false;
            uploadButton.disabled = false;
            submitButtons.forEach((button) => button.disabled = false);
        }
    }

    uploadButton.addEventListener("click", () => input.click());
    input.addEventListener("change", () => {
        void uploadImages(Array.from(input.files));
        input.value = "";
    });
    dropzone.addEventListener("dragover", (event) => {
        event.preventDefault();
        dropzone.classList.add("is-dragging");
    });
    dropzone.addEventListener("dragleave", (event) => {
        if (!dropzone.contains(event.relatedTarget)) {
            dropzone.classList.remove("is-dragging");
        }
    });
    dropzone.addEventListener("drop", (event) => {
        event.preventDefault();
        dropzone.classList.remove("is-dragging");
        void uploadImages(Array.from(event.dataTransfer.files));
    });
}
