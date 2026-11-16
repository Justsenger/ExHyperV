import { Spherical, Vector3 } from 'three';
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

  const offset = new Vector3();
  const spherical = new Spherical();
  let velTheta = 0;
  let velPhi = 0;
  let prevTheta = null;
  let prevPhi = null;
  let lastMoveTime = 0;
  let lastWheelTime = 0;
  let pointerActive = false;
  let applyingInertia = false;

  const clampVel = (v) => Math.max(-8.5, Math.min(8.5, v));

  const readSpherical = () => {
    offset.copy(camera.position).sub(controls.target);
    spherical.setFromVector3(offset);
  };

  const applySpherical = () => {
    spherical.phi = Math.max(
      controls.minPolarAngle,
      Math.min(controls.maxPolarAngle, spherical.phi)
    );
    offset.setFromSpherical(spherical);
    camera.position.copy(controls.target).add(offset);
    applyingInertia = true;
    controls.update();
    applyingInertia = false;
  };

  controls.addEventListener('start', () => {
    pointerActive = true;
    velTheta = 0;
    velPhi = 0;
    readSpherical();
    prevTheta = spherical.theta;
    prevPhi = spherical.phi;
    lastMoveTime = performance.now();
  });

  controls.addEventListener('change', () => {
    if (!pointerActive || applyingInertia) return;
    readSpherical();
    const now = performance.now();
    const dtMove = Math.max(0.004, Math.min(0.06, (now - lastMoveTime) / 1000 || 0.016));
    lastMoveTime = now;
    if (prevTheta !== null && prevPhi !== null) {
      let dTheta = spherical.theta - prevTheta;
      if (dTheta > Math.PI) dTheta -= Math.PI * 2;
      if (dTheta < -Math.PI) dTheta += Math.PI * 2;
      const dPhi = spherical.phi - prevPhi;
      // Convert per-event delta to true radians/sec with a 1.15x flick boost
      const instVelTheta = (dTheta / dtMove) * 1.15;
      const instVelPhi = (dPhi / dtMove) * 1.15;
      velTheta = clampVel(velTheta * 0.35 + instVelTheta * 0.65);
      velPhi = clampVel(velPhi * 0.35 + instVelPhi * 0.65);
    }
    prevTheta = spherical.theta;
    prevPhi = spherical.phi;
  });

  controls.addEventListener('end', () => {
    pointerActive = false;
    prevTheta = null;
    prevPhi = null;
    // If the user held the pointer completely still for >95ms before releasing, damp release velocity
    const pauseSec = Math.max(0, (performance.now() - lastMoveTime) / 1000 - 0.095);
    if (pauseSec > 0) {
      const holdDecay = Math.exp(-pauseSec * 16);
      velTheta *= holdDecay;
      velPhi *= holdDecay;
    }
  });

  function onWheel(event) {
    if (event.ctrlKey) return;
    event.preventDefault();
    event.stopPropagation();
    const modeScale = event.deltaMode === 1 ? 24 : (event.deltaMode === 2 ? 240 : 1);
    // Clamp burst spikes from mouse wheels while preserving high-precision touchpad deltas
    const rawDx = (event.deltaX || 0) * modeScale;
    const rawDy = (event.deltaY || 0) * modeScale;
    if (!rawDx && !rawDy) return;
    const dx = Math.max(-140, Math.min(140, rawDx));
    const dy = Math.max(-140, Math.min(140, rawDy));

    readSpherical();
    // Natural touchpad direction: two-finger swipe right (deltaX < 0) rotates cube right (theta decreases),
    // two-finger swipe down (deltaY < 0) tilts cube down (phi decreases), matching 1:1 finger drag.
    const speed = 0.0052;
    const dTheta = dx * speed;
    const dPhi = dy * speed;
    spherical.theta += dTheta;
    spherical.phi += dPhi;
    applySpherical();

    const now = performance.now();
    const dtWheel = Math.max(0.006, Math.min(0.06, (now - lastWheelTime) / 1000 || 0.016));
    lastWheelTime = now;
    // Feed smooth rotational inertia for long, silky touchpad coasting
    velTheta = clampVel(velTheta * 0.42 + (dTheta / dtWheel) * 0.68);
    velPhi = clampVel(velPhi * 0.42 + (dPhi / dtWheel) * 0.68);

    controls.dispatchEvent({ type: 'wheel-orbit' });
  }

  controls.updateInertia = (dt) => {
    if (pointerActive) return false;
    if (Math.abs(velTheta) < 5e-4 && Math.abs(velPhi) < 5e-4) {
      velTheta = 0;
      velPhi = 0;
      return false;
    }
    readSpherical();
    spherical.theta += velTheta * dt;
    spherical.phi += velPhi * dt;
    applySpherical();
    // Long, silky exponential damping (tau ~ 0.40s -> ~1.1s smooth ease-out glide)
    const decay = Math.exp(-dt * 2.55);
    velTheta *= decay;
    velPhi *= decay;
    return Math.abs(velTheta) > 0.015 || Math.abs(velPhi) > 0.015;
  };

  const baseReset = controls.reset.bind(controls);
  controls.reset = () => {
    velTheta = 0;
    velPhi = 0;
    prevTheta = null;
    prevPhi = null;
    baseReset();
  };

  canvas.addEventListener('wheel', onWheel, { passive: false });
  const baseDispose = controls.dispose.bind(controls);
  controls.dispose = () => {
    canvas.removeEventListener('wheel', onWheel);
    baseDispose();
  };

  return controls;
}
