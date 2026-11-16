import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { OrthographicCamera, Vector3 } from 'three';
import { createHomeModelControls } from '../src/scripts/home-model-controls.js';

class Canvas extends EventTarget {
  ownerDocument = new EventTarget();
  style = {};
  clientWidth = 360;
  clientHeight = 360;
  setPointerCapture() {}
  releasePointerCapture() {}
  getBoundingClientRect() { return { left: 0, top: 0, width: 360, height: 360 }; }
}

function pointer(target, type, pointerType, x, y) {
  const event = new Event(type, { cancelable: true });
  Object.assign(event, { pointerId: 1, pointerType, button: 0, clientX: x, clientY: y, pageX: x, pageY: y });
  target.dispatchEvent(event);
}

for (const pointerType of ['mouse', 'touch']) {
  test(`${pointerType} drag rotates the icon while keeping its center and scale`, () => {
    const canvas = new Canvas();
    const camera = new OrthographicCamera(-4, 4, 4, -4, 0.1, 100);
    camera.position.set(6, 8, 10);
    const controls = createHomeModelControls(camera, canvas, new Vector3(0, 2, 0));
    const start = camera.position.clone();
    const distance = camera.position.distanceTo(controls.target);
    pointer(canvas, 'pointerdown', pointerType, 100, 160);
    pointer(canvas.ownerDocument, 'pointermove', pointerType, 190, 185);
    pointer(canvas.ownerDocument, 'pointerup', pointerType, 190, 185);
    assert.ok(camera.position.distanceTo(start) > 1);
    assert.ok(Math.abs(camera.position.distanceTo(controls.target) - distance) < 1e-8);
    assert.deepEqual(controls.target.toArray(), [0, 2, 0]);
    assert.equal(camera.zoom, 1);
    controls.reset();
    assert.ok(camera.position.distanceTo(start) < 1e-8);
    controls.dispose();
  });
}

test('trackpad wheel gestures over the icon orbit the model instead of scrolling the page', () => {
  const canvas = new Canvas();
  const camera = new OrthographicCamera(-4, 4, 4, -4, 0.1, 100);
  camera.position.set(6, 8, 10);
  const controls = createHomeModelControls(camera, canvas, new Vector3(0, 2, 0));
  const start = camera.position.clone();
  const distance = camera.position.distanceTo(controls.target);

  // Measure pointer drag right (+X) and down (+Y) direction
  pointer(canvas, 'pointerdown', 'mouse', 100, 160);
  pointer(canvas.ownerDocument, 'pointermove', 'mouse', 180, 220);
  pointer(canvas.ownerDocument, 'pointerup', 'mouse', 180, 220);
  const dragDelta = camera.position.clone().sub(start);
  controls.reset();

  // Natural-scroll trackpad two-finger swipe right (deltaX < 0) and down (deltaY < 0)
  // must orbit in the exact same direction as pointer drag right/down
  const wheel = new Event('wheel', { cancelable: true });
  Object.assign(wheel, { deltaX: -80, deltaY: -120, deltaMode: 0, ctrlKey: false });
  canvas.dispatchEvent(wheel);
  const afterWheel = camera.position.clone();
  const wheelDelta = afterWheel.clone().sub(start);
  assert.equal(wheel.defaultPrevented, true);
  assert.ok(afterWheel.distanceTo(start) > 0.5);
  assert.equal(Math.sign(wheelDelta.x), Math.sign(dragDelta.x));
  assert.equal(Math.sign(wheelDelta.y), Math.sign(dragDelta.y));
  assert.ok(Math.abs(camera.position.distanceTo(controls.target) - distance) < 1e-8);
  assert.equal(camera.zoom, 1);

  // Rotational inertia continues coasting smoothly in the same direction
  controls.updateInertia(0.016);
  assert.ok(camera.position.distanceTo(start) > afterWheel.distanceTo(start));

  const pinch = new Event('wheel', { cancelable: true });
  Object.assign(pinch, { deltaY: -40, deltaMode: 0, ctrlKey: true });
  canvas.dispatchEvent(pinch);
  assert.equal(pinch.defaultPrevented, false);

  controls.reset();
  assert.ok(camera.position.distanceTo(start) < 1e-8);
  controls.dispose();
});

test('the exported icon contains eight textured 3D cells and the source camera', async () => {
  const buffer = await readFile(new URL('../public/models/exhyperv-icon.glb', import.meta.url));
  assert.equal(buffer.subarray(0, 4).toString(), 'glTF');
  assert.equal(buffer.readUInt32LE(4), 2);
  assert.equal(buffer.readUInt32LE(8), buffer.length);
  const json = JSON.parse(buffer.subarray(20, 20 + buffer.readUInt32LE(12)).toString());
  assert.equal(json.meshes.length, 8);
  assert.equal(json.images.length, 8);
  assert.equal(json.cameras[0].type, 'orthographic');
  const { xmag, ymag } = json.cameras[0].orthographic;
  assert.ok(Math.abs(xmag - ymag) < 1e-3, `Expected 1:1 orthographic camera aspect, got ${xmag}x${ymag}`);
  for (const mesh of json.meshes) {
    const positions = json.accessors[mesh.primitives[0].attributes.POSITION];
    assert.ok(positions.count > 8);
    assert.ok(positions.max.every((max, axis) => max - positions.min[axis] > 0.1));
    const material = json.materials[mesh.primitives[0].material];
    assert.ok(material.emissiveTexture);
  }
  assert.ok(json.images.every(image => Number.isInteger(image.bufferView) && !image.uri));
  assert.ok(!JSON.stringify(json).includes('Users'));
});
