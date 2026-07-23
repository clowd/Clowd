// End-to-end smoke test for the clowd_server Rust worker (wrangler dev, discard destination).
import { createHash } from 'node:crypto';

const BASE = process.env.E2E_BASE ?? 'http://127.0.0.1:8787';
const MiB = 1024 * 1024;
const TOTAL = 40 * MiB; // ~40MB synthetic file

let failures = 0;
function check(name, cond, detail = '') {
  const ok = !!cond;
  console.log(`${ok ? 'PASS' : 'FAIL'}: ${name}${detail ? ' — ' + detail : ''}`);
  if (!ok) failures++;
  return ok;
}
function sha(buf) { return createHash('sha256').update(buf).digest('hex'); }
const sleep = ms => new Promise(r => setTimeout(r, ms));

// deterministic synthetic data
const data = Buffer.alloc(TOTAL);
for (let i = 0; i < TOTAL; i++) data[i] = (i * 31 + (i >> 13)) & 0xff;

async function main() {
  // 1) healthz
  const hz = await fetch(`${BASE}/healthz`);
  check('healthz 200', hz.status === 200, `status=${hz.status} body=${await hz.text()}`);

  // 2) / -> 301
  const root = await fetch(`${BASE}/`, { redirect: 'manual' });
  check('/ -> 301', root.status === 301, `status=${root.status} location=${root.headers.get('location')}`);

  // 3) create upload (discard destination)
  const createResp = await fetch(`${BASE}/api/v1/uploads`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      fileName: 'synthetic.bin',
      contentType: 'application/octet-stream',
      contentLength: TOTAL,
      destination: { type: 'discard', finalUrl: 'https://example.com/synthetic.bin' },
    }),
  });
  const createBody = await createResp.json().catch(() => ({}));
  check('create upload 201', createResp.status === 201, `status=${createResp.status} body=${JSON.stringify(createBody)}`);
  const { id, uploadToken, deleteToken, chunkSize, chunkCount, downloadUrl } = createBody;
  check('create returns id/tokens/plan', !!(id && uploadToken && deleteToken && chunkSize && chunkCount && downloadUrl),
        JSON.stringify({ id, chunkSize, chunkCount, downloadUrl }));
  if (!id) throw new Error('no id; aborting');

  const chunks = [];
  for (let n = 0; n * chunkSize < TOTAL; n++) chunks.push(data.subarray(n * chunkSize, Math.min((n + 1) * chunkSize, TOTAL)));
  check('chunkCount matches plan', chunks.length === chunkCount, `local=${chunks.length} server=${chunkCount}`);

  async function putChunk(n, token = uploadToken) {
    return fetch(`${BASE}/api/v1/uploads/${id}/chunks/${n}`, {
      method: 'PUT',
      headers: { authorization: `Bearer ${token}`, 'content-type': 'application/octet-stream' },
      body: chunks[n],
    });
  }

  // 4) upload 2 chunks
  for (const n of [0, 1]) {
    const r = await putChunk(n);
    const b = await r.text();
    check(`PUT chunk ${n} -> 200`, r.status === 200, `status=${r.status} body=${b}`);
  }

  // 5) mid-upload tail on a second connection
  const uploadedSoFar = chunks[0].length + chunks[1].length;
  const tailAbort = new AbortController();
  const tailResp = await fetch(`${BASE}/u/${id}`, { signal: tailAbort.signal });
  check('mid-upload tail status 200', tailResp.status === 200, `status=${tailResp.status}`);
  check('tail Content-Length = declared total', tailResp.headers.get('content-length') === String(TOTAL),
        `content-length=${tailResp.headers.get('content-length')} expected=${TOTAL}`);
  console.log(`  tail headers: content-type=${tailResp.headers.get('content-type')} ` +
              `content-disposition=${tailResp.headers.get('content-disposition')} ` +
              `cache-control=${tailResp.headers.get('cache-control')} accept-ranges=${tailResp.headers.get('accept-ranges')}`);

  const reader = tailResp.body.getReader();
  const received = [];
  let receivedLen = 0;
  let streamDone = false;
  let readErr = null;
  let pendingRead = null;
  function pump() {
    pendingRead = reader.read().then(({ done, value }) => {
      if (done) { streamDone = true; return; }
      received.push(Buffer.from(value));
      receivedLen += value.length;
      pump();
    }).catch(e => { readErr = e; });
  }
  pump();

  // wait up to 30s for exactly the bytes so far
  const deadline = Date.now() + 30000;
  while (receivedLen < uploadedSoFar && !streamDone && !readErr && Date.now() < deadline) await sleep(100);
  check('tail received bytes-so-far (2 chunks)', receivedLen === uploadedSoFar, `received=${receivedLen} expected=${uploadedSoFar}`);

  // quiet period: no extra bytes, stream stays open
  const lenBefore = receivedLen;
  await sleep(2500);
  check('tail delivers no extra bytes while idle', receivedLen === lenBefore, `received=${receivedLen}`);
  check('tail stays open mid-upload', !streamDone && !readErr, `done=${streamDone} err=${readErr}`);
  const partial = Buffer.concat(received);
  check('tail bytes-so-far content matches', sha(partial.subarray(0, uploadedSoFar)) === sha(data.subarray(0, uploadedSoFar)));

  // 6) upload the rest
  for (let n = 2; n < chunks.length; n++) {
    const r = await putChunk(n);
    check(`PUT chunk ${n} -> 200`, r.status === 200, `status=${r.status}`);
  }

  // 7) tail completes with full content
  const deadline2 = Date.now() + 60000;
  while (!streamDone && !readErr && Date.now() < deadline2) await sleep(100);
  check('tail stream ended cleanly after final chunk', streamDone && !readErr, `done=${streamDone} err=${readErr} received=${receivedLen}`);
  const full = Buffer.concat(received);
  check('tail full length', full.length === TOTAL, `received=${full.length} expected=${TOTAL}`);
  check('tail full content sha256 matches', sha(full) === sha(data), `got=${sha(full).slice(0, 16)}… want=${sha(data).slice(0, 16)}…`);

  // 8) complete -> 200
  const comp = await fetch(`${BASE}/api/v1/uploads/${id}/complete`, {
    method: 'POST', headers: { authorization: `Bearer ${uploadToken}` },
  });
  const compBody = await comp.json().catch(() => ({}));
  check('complete -> 200', comp.status === 200, `status=${comp.status} body=${JSON.stringify(compBody)}`);
  check('complete returns finalUrl+length', compBody.finalUrl === 'https://example.com/synthetic.bin' && compBody.length === TOTAL,
        JSON.stringify(compBody));

  // 9) /u/{id} -> 301
  const red = await fetch(`${BASE}/u/${id}`, { redirect: 'manual' });
  check('/u/{id} -> 301 after complete', red.status === 301, `status=${red.status}`);
  check('301 location = finalUrl', red.headers.get('location') === 'https://example.com/synthetic.bin',
        `location=${red.headers.get('location')}`);

  // 10) wrong bearer -> 401/403
  const badPut = await putChunk(0, 'wrong-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  check('wrong bearer on chunk PUT -> 401/403', badPut.status === 401 || badPut.status === 403, `status=${badPut.status}`);
  const badDel = await fetch(`${BASE}/api/v1/uploads/${id}`, {
    method: 'DELETE', headers: { authorization: 'Bearer wrong-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' },
  });
  check('wrong bearer on DELETE -> 401/403', badDel.status === 401 || badDel.status === 403, `status=${badDel.status}`);

  // 11) DELETE with deleteToken, then /u/{id} -> 404
  const del = await fetch(`${BASE}/api/v1/uploads/${id}`, {
    method: 'DELETE', headers: { authorization: `Bearer ${deleteToken}` },
  });
  check('DELETE with deleteToken succeeds', del.status >= 200 && del.status < 300, `status=${del.status}`);
  const after = await fetch(`${BASE}/u/${id}`, { redirect: 'manual' });
  check('/u/{id} -> 404 after DELETE', after.status === 404, `status=${after.status}`);

  console.log(failures === 0 ? '\nALL ASSERTIONS PASSED' : `\n${failures} ASSERTION(S) FAILED`);
  process.exit(failures === 0 ? 0 : 1);
}

main().catch(e => { console.error('SCRIPT ERROR:', e); process.exit(2); });
