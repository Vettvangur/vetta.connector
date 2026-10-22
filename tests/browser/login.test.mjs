import {readFile} from 'node:fs/promises';
import test from 'node:test';
import assert from 'node:assert/strict';
const source = await readFile(new URL('../../src/Vetta.Connector.AspNetCore/vetta-login.js', import.meta.url), 'utf8');
const {VettaLogin, VettaPasskeys} = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
globalThis.location = {origin: 'https://client.example'};
const response = value => ({ok: true, json: async () => value});

test('rejects endpoints outside the client origin', () => {
    assert.throws(() => new VettaLogin('https://other.example/auth'));
});
test('unavailable status stops without retry and retains attempt for explicit recovery', async () => {
    const calls = [];
    globalThis.fetch = async url => {
        calls.push(url);
        return response(url.endsWith('/session') ? {token: 'csrf'} : url.endsWith('/start') ?
            {state: 'Started', handle: 'opaque', challenge: '1234'} : {state: 'Unavailable'});
    };
    const login = new VettaLogin();
    await login.start({method: 2, phoneNumberOrSsn: 'fixture'});
    assert.equal((await login.wait()).state, 'Unavailable');
    assert.equal(login.handle, 'opaque');
    assert.equal(calls.filter(url => url.endsWith('/status')).length, 1);
});
test('concurrent wait is rejected and cancellation ignores late success', async () => {
    let resolveStatus;
    globalThis.fetch = async url => {
        if (url.endsWith('/session')) return response({token: 'csrf'});
        if (url.endsWith('/start')) return response({state: 'Started', handle: 'opaque'});
        if (url.endsWith('/cancel')) return response({state: 'Cancelled'});
        return new Promise(resolve => { resolveStatus = resolve; });
    };
    const login = new VettaLogin();
    await login.start({});
    let updates = 0;
    const wait = login.wait(() => updates++);
    await assert.rejects(login.wait());
    assert.equal((await login.cancel()).state, 'Cancelled');
    resolveStatus(response({state: 'Completed', redirect: '/account'}));
    assert.equal((await wait).state, 'Cancelled');
    assert.equal(updates, 0);
});

test('passkeys use native credentials and a same-origin finish without exposing binding', async () => {
    const handle='A'.repeat(64); const calls=[];
    globalThis.PublicKeyCredential={parseCreationOptionsFromJSON:x=>x,parseRequestOptionsFromJSON:x=>x};
    Object.defineProperty(globalThis,'navigator',{configurable:true,value:{credentials:{get:async()=>({toJSON:()=>({id:'credential'})})}}});
    globalThis.fetch=async (url,options)=>{
        calls.push({url,options});
        if(url.endsWith('/session')) return response({token:'csrf'});
        if(url.endsWith('/options')) return response({state:'Options',handle,optionsJson:'{}'});
        return response({state:'Completed',redirect:'/account'});
    };
    const result=await new VettaPasskeys().authenticate();
    assert.equal(result.state,'Completed');
    const finish=JSON.parse(calls.at(-1).options.body);
    assert.deepEqual(finish,{handle,credentialJson:'{"id":"credential"}'});
});
test('native cancellation cancels the bound ceremony without attempting completion', async () => {
    const calls=[];
    globalThis.PublicKeyCredential={parseCreationOptionsFromJSON:x=>x,parseRequestOptionsFromJSON:x=>x};
    Object.defineProperty(globalThis,'navigator',{configurable:true,value:{credentials:{get:async()=>{throw new Error('cancelled');}}}});
    globalThis.fetch=async url=>{
        calls.push(url);
        return response(url.endsWith('/session')?{token:'csrf'}:url.endsWith('/options')?{state:'Options',handle:'A'.repeat(64),optionsJson:'{}'}:{state:'Cancelled'});
    };
    await assert.rejects(new VettaPasskeys().authenticate());
    assert.equal(calls.some(x=>x.endsWith('/finish')),false);
    assert.equal(calls.at(-1).endsWith('/cancel'),true);
});
