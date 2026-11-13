// Shared response handling for long-running gallery path renames, moves and reloads.
window.DimDockPathOperation = (() => {
    const proxyTimeoutStatuses = new Set([502, 503, 504, 520, 522, 524]);

    async function errorMessage(response, fallback) {
        if (proxyTimeoutStatuses.has(response.status)) {
            return "The server took too long to respond. The operation may still be finishing; refresh the page in a minute to check.";
        }
        const contentType = response.headers.get("content-type") || "";
        if (contentType.startsWith("text/plain")) {
            const text = (await response.text()).trim();
            if (text && text.length <= 500) {
                return text;
            }
        }
        return `${fallback} (HTTP ${response.status})`;
    }

    async function complete(response, fallback, onProgress) {
        if (!response.ok) {
            throw new Error(await errorMessage(response, fallback));
        }
        if (response.status !== 202) {
            return await response.json();
        }
        const { operationId } = await response.json();
        const statusUrl = new URL(window.location.href);
        statusUrl.search = "";
        statusUrl.searchParams.set("handler", "PathOperation");
        statusUrl.searchParams.set("id", operationId);
        const started = Date.now();
        let failures = 0;
        for (;;) {
            onProgress?.(`Still working (${Math.round((Date.now() - started) / 1000)}s). Keep this page open...`);
            await new Promise(resolve => window.setTimeout(resolve, 2000));
            let status;
            try {
                status = await fetch(statusUrl, { headers: { "X-Requested-With": "XMLHttpRequest" } });
            } catch {
                status = null;
            }
            if (status?.status === 202) {
                failures = 0;
                continue;
            }
            if (status?.ok) {
                return await status.json();
            }
            if (status && !proxyTimeoutStatuses.has(status.status)) {
                throw new Error(await errorMessage(status, fallback));
            }
            if (++failures >= 5) {
                throw new Error("Lost contact with the server. The operation may still be finishing; refresh the page in a minute to check.");
            }
        }
    }

    return { errorMessage, complete };
})();
