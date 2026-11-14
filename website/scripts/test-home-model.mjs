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

test('scrolling over the icon remains page scrolling', () => {
  const canvas = new Canvas();
  const camera = new OrthographicCamera(-4, 4, 4, -4, 0.1, 100);
  camera.position.set(6, 8, 10);
  const controls = createHomeModelControls(camera, canvas, new Vector3());
  const wheel = new Event('wheel', { cancelable: true });
  Object.assign(wheel, { deltaY: 100, deltaMode: 0 });
  canvas.dispatchEvent(wheel);
  assert.equal(wheel.defaultPrevented, false);
  assert.equal(camera.zoom, 1);
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
