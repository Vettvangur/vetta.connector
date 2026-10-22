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
