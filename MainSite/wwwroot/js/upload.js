// Multiple-file uploads in the admin pages: the form is the drop target and
// files dropped anywhere on the page go through its file input. Everything
// still works without JavaScript: pick files and press Upload.
(() => {
    "use strict";

    const form = document.querySelector("[data-upload-form]");
    if (!form) return;

    const input = form.querySelector('input[type="file"]');
    if (!input) return;

    const button = form.querySelector('button[type="submit"]');
    const status = form.querySelector("[data-upload-status]");
    const maxBytes = Number(form.dataset.maxBytes) || 0;

    let depth = 0;

    const hasFiles = (event) => {
        const types = event.dataTransfer && event.dataTransfer.types;
        return !!types && Array.from(types).includes("Files");
    };

    const setStatus = (text) => {
        if (!status) return;
        status.textContent = text;
        status.hidden = !text;
    };

    const matchesAccept = (file) => {
        const rules = (input.accept || "")
            .split(",")
            .map((rule) => rule.trim().toLowerCase())
            .filter(Boolean);
        if (rules.length === 0) return true;

        const name = file.name.toLowerCase();
        const type = (file.type || "").toLowerCase();

        return rules.some((rule) => {
            if (rule.startsWith(".")) return name.endsWith(rule);
            if (rule.endsWith("/*")) return type.startsWith(rule.slice(0, rule.length - 1));
            return type === rule;
        });
    };

    const formatSize = (bytes) => bytes >= 1024 * 1024
        ? `${Math.round(bytes / (1024 * 1024))} MB`
        : `${Math.max(1, Math.round(bytes / 1024))} KB`;

    const markUploading = () => {
        if (!button) return;
        button.disabled = true;
        button.textContent = "Uploading…";
    };

    const send = () => {
        markUploading();
        if (typeof form.requestSubmit === "function") {
            form.requestSubmit();
        } else {
            form.submit();
        }
    };

    const upload = (files) => {
        if (button && button.disabled) return;

        const accepted = files.filter(matchesAccept);
        const skipped = files.length - accepted.length;

        const transfer = new DataTransfer();
        accepted.forEach((file) => transfer.items.add(file));
        input.files = transfer.files;

        if (accepted.length === 0) {
            setStatus("No supported files in that selection.");
            return;
        }

        const total = accepted.reduce((sum, file) => sum + file.size, 0);
        if (maxBytes > 0 && total > maxBytes) {
            setStatus(`Those files are ${formatSize(total)}: one upload can carry at most ${formatSize(maxBytes)}.`);
            return;
        }

        if (skipped > 0) {
            // Submitting here would reload the page and lose this note, so the
            // user presses Upload for the files that are left.
            setStatus(`${skipped} file(s) skipped (unsupported type). Press Upload to send the other ${accepted.length}.`);
            return;
        }

        setStatus(accepted.length === 1 ? "Uploading 1 file…" : `Uploading ${accepted.length} files…`);
        send();
    };

    form.addEventListener("dragenter", (event) => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        depth += 1;
        form.classList.add("is-dragover");
    });

    form.addEventListener("dragover", (event) => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "copy";
    });

    form.addEventListener("dragleave", () => {
        depth = Math.max(0, depth - 1);
        if (depth === 0) form.classList.remove("is-dragover");
    });

    // The whole page accepts the drop, not just the dashed box: dragging a
    // file over the page must never make the browser open it instead.
    document.addEventListener("dragover", (event) => {
        if (hasFiles(event)) event.preventDefault();
    });

    // Leaving the window mid-drag can skip the form's own dragleave: without
    // this the dashed box would stay highlighted.
    document.addEventListener("dragleave", (event) => {
        if (event.relatedTarget) return;
        depth = 0;
        form.classList.remove("is-dragover");
    });

    document.addEventListener("drop", (event) => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        depth = 0;
        form.classList.remove("is-dragover");

        const files = Array.from(event.dataTransfer.files);
        if (files.length === 0) {
            setStatus("No files in that drop.");
            return;
        }

        upload(files);
    });

    // A click on Upload follows the same path as a drop: disable the button so
    // a slow upload cannot be sent twice.
    form.addEventListener("submit", markUploading);
})();
