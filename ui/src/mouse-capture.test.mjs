import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const source = readFileSync(new URL("../vendor/novnc/core/util/events.js", import.meta.url), "utf8")
  .replace(/export /g, "");
class Mouse extends Event {
  constructor(type, init = {}) {
    super(type, { cancelable: true });
    this.buttons = init.buttons ?? 0;
    this.clientX = init.clientX ?? 40;
    this.clientY = init.clientY ?? 50;
    this.deltaY = type === "wheel" ? (init.deltaY ?? 0) : 0;
  }
}
function fixture() {
  const win = new EventTarget(), target = new EventTarget();
  let overlay = null;
  const doc = {
    getElementById: () => overlay,
    createElement: () => Object.assign(new EventTarget(), { style: {} }),
    body: { appendChild: el => { overlay = el; } },
  };
  win.getComputedStyle = () => ({ cursor: "default" });
  const context = { document: doc, window: win, MouseEvent: Mouse,
    MutationObserver: class { observe() {} disconnect() {} } };
  const capture = vm.runInNewContext(source + "\n({setCapture,releaseCapture})", context);
  const events = [];
  for (const type of ["mouseup", "mousemove", "wheel"]) {
    target.addEventListener(type, e => { events.push([type, e.buttons, e.deltaY]); e.preventDefault(); });
  }
  capture.setCapture(target);
  return { doc, target, events, overlay: () => overlay,
    send(type, init) { const e = new Mouse(type, init); overlay.dispatchEvent(e); return e; } };
}
test("a lost mouse-up is recovered when the pointer returns with no buttons", () => {
  const f = fixture();
  f.send("mousemove", { buttons: 0 });
  assert.equal(f.doc.captureElement, null);
  assert.equal(f.overlay().style.display, "none");
  assert.deepEqual(f.events.map(e => e[0]), ["mouseup", "mousemove"]);
});
test("wheel over a stale capture reaches the canvas without another click", () => {
  const f = fixture();
  const e = f.send("wheel", { buttons: 0, deltaY: 120 });
  assert.equal(f.doc.captureElement, null);
  assert.deepEqual(f.events, [["mouseup", 0, 0], ["wheel", 0, 120]]);
  assert.equal(e.defaultPrevented, true);
});
test("wheel during an actual drag preserves capture and held buttons", () => {
  const f = fixture();
  f.send("wheel", { buttons: 1, deltaY: -120 });
  assert.equal(f.doc.captureElement, f.target);
  assert.deepEqual(f.events, [["wheel", 1, -120]]);
});
test("moving while a button is held does not end a drag", () => {
  const f = fixture();
  f.send("mousemove", { buttons: 1 });
  assert.equal(f.doc.captureElement, f.target);
  assert.deepEqual(f.events, [["mousemove", 1, 0]]);
});
