export function getOrigin() {
    return globalThis.location.origin;
}

// The fragment is not sent to the server. Consume it once and remove it from
// this history entry; never put a sample credential in preferences or a query.
export function takeDemoToken() {
    const parameters = new URLSearchParams(globalThis.location.hash.slice(1));
    if (!parameters.has("demo")) return null;
    const token = parameters.get("demo");
    globalThis.history.replaceState(null, "", globalThis.location.pathname + globalThis.location.search);
    const local = ["127.0.0.1", "localhost", "[::1]"].includes(globalThis.location.hostname);
    return local && /^[A-Fa-f0-9]{64}$/.test(token || "") ? token : null;
}

export function getPreference(key) {
    return globalThis.localStorage.getItem(key);
}

export function setPreference(key, value) {
    globalThis.localStorage.setItem(key, value);
}

export function downloadPresets(json) {
    const url = URL.createObjectURL(new Blob([json], { type: "application/json" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "entityforge-query-presets.json";
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}
export function importPresets() {
    return new Promise((resolve, reject) => {
        const input = document.createElement("input");
        input.type = "file";
        input.accept = ".json,application/json";
        input.style.display = "none";
        document.body.append(input);
        input.addEventListener("cancel", () => { input.remove(); resolve(null); }, { once: true });
        input.addEventListener("change", async () => {
            try {
                const file = input.files[0];
                if (file && file.size > 2 * 1024 * 1024) throw new Error("Preset files must be smaller than 2 MiB.");
                resolve(file ? await file.text() : null);
            } catch (error) { reject(error); }
            finally { input.remove(); }
        }, { once: true });
        input.click();
    });
}
