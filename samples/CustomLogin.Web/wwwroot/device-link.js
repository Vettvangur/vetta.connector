import { VettaDeviceLinks } from '/auth/vetta/client.js';
const flow = new VettaDeviceLinks();
const qr = document.querySelector('#qr'), app = document.querySelector('#open-app');
const message = document.querySelector('#message'), cancel = document.querySelector('#cancel');
const buttons = [document.querySelector('#qr-start'), document.querySelector('#app-start')];
let stopQr = () => {};
const errors = { user_not_found: 'No account was found in the Auðkenni app.', rejected: 'You declined the request.', timeout: 'The request expired. Please try again.', credential_blocked: 'This credential is blocked. Choose another sign-in method.' };
function clean() { stopQr(); qr.hidden = true; app.hidden = true; }
function controls() { buttons.forEach(button => button.disabled = !!flow.handle || flow.running); cancel.hidden = !flow.handle; }
if (new URLSearchParams(location.search).has('vettaDevice')) message.textContent = 'The app sign-in could not be completed. Please start again in the same browser.';
async function start(mobile) {
    clean(); buttons.forEach(button => button.disabled = true);
    try {
        const result = mobile ? await flow.startWeb2App() : await flow.startQr();
        if (result.state !== 'Started') { message.textContent = errors[result.errorCode] ?? 'Sign-in is unavailable. Please try again shortly.'; return; }
        cancel.hidden = false;
        if (mobile) {
            app.href = /Android/i.test(navigator.userAgent) ? result.androidAppLink : result.appLink;
            app.hidden = false; message.textContent = 'Open the app to approve. Return to this same browser afterwards.';
        } else {
            qr.hidden = false; message.textContent = 'Scan the code with the Auðkenni app.';
            stopQr = flow.refreshQr(qr, result.qrImageUrl, () => { message.textContent = 'The QR code could not be refreshed. Cancel and start again.'; });
        }
        const completed = await flow.wait();
        message.textContent = errors[completed.errorCode] ?? (completed.state === 'Completed' ? 'Signed in. Redirecting…' : 'Sign-in ended. Please try again.');
        if (completed.state === 'Completed') location.assign(completed.redirect);
    } catch (error) { message.textContent = error.name === 'AbortError' && !flow.handle ? 'Cancelled.' : 'Connection interrupted. Cancel before starting again.'; }
    finally { clean(); controls(); }
}
buttons[0].addEventListener('click', () => start(false));
buttons[1].addEventListener('click', () => start(true));
cancel.addEventListener('click', async () => {
    try { const result = await flow.cancel(); message.textContent = result.state === 'Busy' ? 'Sign-in is completing. Please wait.' : 'Cancelled.'; }
    catch { message.textContent = 'Cancellation could not be confirmed. Please try again.'; }
    if (!flow.handle) clean(); controls();
});
addEventListener('pagehide', () => { stopQr(); flow.abort?.abort(); });
