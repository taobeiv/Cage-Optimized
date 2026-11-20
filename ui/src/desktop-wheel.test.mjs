import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// Run the actual legacy RFB handler with a mouse-message recorder; no UI/VM.
const source = readFileSync(new URL("../vendor/novnc/core/rfb.js", import.meta.url), "utf8");
const start = source.indexOf("    _cageStepWheel(x, y, bmask, dX, dY) {");
assert.ok(start >= 0);
const end = source.indexOf("\n    }", start) + "\n    }".length;
const handler = vm.runInNewContext("({" + source.slice(start, end) + "})", { WHEEL_STEP: 50 })._cageStepWheel;
function wheel() {
  const masks = [];
  const state = { _accumulatedWheelDeltaX: 0, _accumulatedWheelDeltaY: 0,
    _handleMouseButton: (_x, _y, mask) => masks.push(mask) };
  return { state, masks, send: (dx, dy, buttons = 0) => handler.call(state, 10, 20, buttons, dx, dy) };
}
test("desktop wheel preserves several notches delivered in one event", () => {
  const f = wheel(); f.send(0, 400);
  assert.equal(f.masks.filter(mask => mask === 16).length, 8);
  assert.equal(f.state._accumulatedWheelDeltaY, 0);
});
test("desktop wheel retains sub-notch remainder and cancels reverse motion", () => {
  const f = wheel(); f.send(0, 70); f.send(0, -10);
  assert.equal(f.state._accumulatedWheelDeltaY, 10);
  f.send(0, 40);
  assert.deepEqual(f.masks, [16, 0, 16, 0]);
});
test("horizontal wheel preserves held buttons on every release", () => {
  const f = wheel(); f.send(-120, 0, 1);
  assert.deepEqual(f.masks, [33, 1, 33, 1]);
  assert.equal(f.state._accumulatedWheelDeltaX, -20);
});
test("a large event has bounded work and leaves the remaining distance for later", () => {
  const f = wheel(); f.send(0, 2000);
  assert.equal(f.masks.length, 32);
  assert.equal(f.state._accumulatedWheelDeltaY, 1200);
  f.send(0, 0); f.send(0, 0);
  assert.equal(f.masks.length, 80);
  assert.equal(f.state._accumulatedWheelDeltaY, 0);
});
test("non-finite wheel deltas cannot poison the accumulator", () => {
  const f = wheel(); f.send(0, Infinity); f.send(NaN, 0); f.send(0, 50);
  assert.deepEqual(f.masks, [16, 0]);
  assert.equal(f.state._accumulatedWheelDeltaY, 0);
});
