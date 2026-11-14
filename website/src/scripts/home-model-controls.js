import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

export function createHomeModelControls(camera, canvas, center) {
  const controls = new OrbitControls(camera, canvas);
  controls.target.copy(center);
  controls.enablePan = false;
  controls.enableZoom = false;
  controls.enableDamping = false;
  controls.rotateSpeed = 0.7;
  controls.minPolarAngle = 0.05;
  controls.maxPolarAngle = Math.PI - 0.05;
  controls.update();
  controls.saveState();
  return controls;
}
