// Import from your site's mapped Vetta endpoint. No API keys or upstream URLs belong here.
export class VettaLogin {
    constructor(base = "/auth/vetta") {
        const url = new URL(base, location.origin);
        if (url.origin !== location.origin || url.search || url.hash) throw new Error("Use a same-origin Vetta endpoint");
        this.base = url.pathname.replace(/\/$/, "");
        this.handle = null;
        this.running = false;
    }
    async initialize() {
        const response = await fetch(`${this.base}/session`, {credentials: "same-origin", cache: "no-store"});
        if (!response.ok) throw new Error("Vetta session unavailable");
        this.token = (await response.json()).token;
    }
    async post(path, data, signal) {
        const response = await fetch(`${this.base}/${path}`, {
            method: "POST", credentials: "same-origin", cache: "no-store", signal,
            headers: {"Content-Type": "application/json", "X-Vetta-CSRF": this.token},
            body: JSON.stringify(data)
        });
        if (!response.ok) throw new Error("Vetta request failed");
        return response.json();
    }
    async start(request) {
        if (this.running || this.handle) throw new Error("Finish or cancel the current attempt first");
        this.running = true;
        try {
            await this.initialize();
            const result = await this.post("start", request);
            if (result.state === "Started") this.handle = result.handle;
            return result;
        } finally { this.running = false; }
    }
    async wait(onUpdate = () => {}) {
        if (this.running || !this.handle) throw new Error("No idle attempt to poll");
        this.running = true;
        const handle = this.handle;
        this.abort = new AbortController();
        try {
            while (this.handle === handle) {
                const result = await this.post("status", {handle}, this.abort.signal);
                if (this.handle !== handle) return {state: "Cancelled"};
                onUpdate(result);
                if (result.state !== "Pending") {
                    if (["Completed", "Failed", "Cancelled", "Expired"].includes(result.state)) this.handle = null;
                    return result;
                }
                await new Promise(resolve => setTimeout(resolve, 500));
            }
            return {state: "Cancelled"};
        } finally { this.running = false; this.abort = null; }
    }
    async cancel() {
        if (!this.handle) return {state: "Cancelled"};
        const handle = this.handle;
        // Ask the backend to cancel before aborting the status request.
        const result = await this.post("cancel", {handle});
        if (["Cancelled", "Expired"].includes(result.state)) {
            this.handle = null;
            this.abort?.abort();
        }
        return result; // Busy means completion may already have started.
    }
}

// Call from a user-initiated button click. Uses the same CSRF/browser session as VettaLogin.
export class VettaPasskeys extends VettaLogin {
    async list(completedLoginHandle) {
        await this.initialize();
        return this.post("passkeys/manage", {completedLoginHandle});
    }
    async remove(completedLoginHandle, credentialId) {
        await this.initialize();
        return this.post("passkeys/manage", {completedLoginHandle, credentialId});
    }
    async authenticate(signal) { return this.ceremony(null, signal); }
    async register(completedLoginHandle, signal) {
        if (typeof completedLoginHandle !== "string" || !/^[A-Fa-f0-9]{64}$/.test(completedLoginHandle))
            throw new Error("A completed Auðkenni login handle is required");
        return this.ceremony(completedLoginHandle, signal);
    }
    async ceremony(completedLoginHandle, signal) {
        if (this.running) throw new Error("A passkey operation is already running");
        if (!globalThis.PublicKeyCredential?.parseCreationOptionsFromJSON ||
            !globalThis.PublicKeyCredential?.parseRequestOptionsFromJSON) throw new Error("Passkeys are not supported in this browser");
        this.running = true;
        let handle;
        try {
            await this.initialize();
            const deadline = signal ? AbortSignal.any([signal, AbortSignal.timeout(120000)]) : AbortSignal.timeout(120000);
            const options = await this.post("passkeys/options", {completedLoginHandle}, deadline);
            if (options.state !== "Options") return options;
            if (!/^[A-Fa-f0-9]{64}$/.test(options.handle) || typeof options.optionsJson !== "string")
                throw new Error("Invalid passkey options");
            handle = options.handle;
            const publicKey = completedLoginHandle
                ? PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(options.optionsJson))
                : PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(options.optionsJson));
            const credential = completedLoginHandle
                ? await navigator.credentials.create({publicKey, signal: deadline})
                : await navigator.credentials.get({publicKey, signal: deadline});
            if (!credential) throw new Error("Passkey operation cancelled");
            const result = await this.post("passkeys/finish", {handle, credentialJson: JSON.stringify(credential.toJSON())}, deadline);
            if (result.redirect && (typeof result.redirect !== "string" || !result.redirect.startsWith("/") ||
                result.redirect.startsWith("//") || result.redirect.includes("\\") || /[\u0000-\u001f\u007f]/.test(result.redirect)))
                throw new Error("Invalid completion redirect");
            return result;
        } catch (error) {
            if (handle) { try { await this.post("passkeys/cancel", {handle}, AbortSignal.timeout(10000)); } catch { /* Retry requires a fresh ceremony. */ } }
            throw error;
        } finally { this.running = false; }
    }
}
