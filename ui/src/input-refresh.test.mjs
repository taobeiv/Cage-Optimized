import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// Exercise the function installed by the production noVNC patch.
const source = readFileSync(new URL("../../internal/cage/novnc-patch.py", import.meta.url), "utf8");
const start = source.indexOf("    async _cageRunInput(action) {");
const end = source.indexOf("\n    }", start) + "\n    }".length;
function fixture() {
  const sent = [];
  const state = { _rfbConnectionState: "connected", _supportsFence: true };
  const RFB = { messages: { clientFence(_sock, flags, id) {
    sent.push(id);
    if (state._cageInputFence?.id === id) {
      clearTimeout(state._cageInputFence.timer);
      state._cageInputFence.resolve();
    }
  } } };
  const run = vm.runInNewContext("({" + source.slice(start, end) + "})", { RFB, setTimeout, clearTimeout })._cageRunInput;
  return { state, sent, run: action => run.call(state, action) };
}
test("completed HTTP input wakes display only after the action resolves", async () => {
  const f = fixture();
  let finish;
  const pending = f.run(() => new Promise(resolve => { finish = resolve; }));
  await Promise.resolve();
  assert.deepEqual(f.sent, ["cage-input-1"]);
  finish("committed");
  assert.equal(await pending, "committed");
  assert.deepEqual(f.sent, ["cage-input-1", "cage-input-complete"]);
});
test("failed input does not announce successful completion", async () => {
  const f = fixture();
  await assert.rejects(f.run(async () => { throw Error("focus changed"); }), /focus changed/);
  assert.deepEqual(f.sent, ["cage-input-1"]);
});
test("disconnect during input does not send to the closed socket", async () => {
  const f = fixture();
  await f.run(async () => { f.state._rfbConnectionState = "disconnected"; });
  assert.deepEqual(f.sent, ["cage-input-1"]);
});
