(() => {
    const dialog = document.querySelector("[data-gallery-age-confirmation]");
    if (!dialog) return;

    let confirmed = dialog.dataset.adultConfirmed === "true";
    let pendingLink = null;
    const form = dialog.querySelector("form");
    const error = dialog.querySelector("[data-gallery-age-error]");
    const submit = form.querySelector("button[type='submit']");

    document.addEventListener("click", (event) => {
        const link = event.target.closest("a");
        if (!link || confirmed || event.defaultPrevented) return;
        const blurredTile = link.matches(".gallery-image-link") && link.closest(".nsfw-blurred");
        const blurredComic = link.matches(".comic-source-link, .comic-download-link") &&
            link.closest("[data-comic-viewer]")?.querySelector(".comic-image-pane.nsfw-blurred");
        if (!blurredTile && !blurredComic) return;
        event.preventDefault();
        pendingLink = link;
        error.textContent = "";
        if (!dialog.open) dialog.showModal();
    });

    dialog.querySelector("[data-cancel-gallery-age]").addEventListener("click", () => dialog.close());
    dialog.addEventListener("close", () => { pendingLink = null; });

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        if (submit.disabled) return;
        submit.disabled = true;
        error.textContent = "";
        try {
            const body = new FormData(form);
            body.set("confirmAdult", "true");
            const response = await fetch(form.action, { method: "POST", body });
            if (!response.ok) throw new Error(await response.text() || "Could not save your confirmation.");
            const result = await response.json();
            if (result.confirmed !== true) throw new Error("Your confirmation was not saved.");
            confirmed = true;
            const link = pendingLink;
            pendingLink = null;
            dialog.close();
            link?.click();
        } catch (failure) {
            error.textContent = failure.message;
        } finally {
            submit.disabled = false;
        }
    });
})();
