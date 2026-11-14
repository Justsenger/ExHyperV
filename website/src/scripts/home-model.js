import { Box3, NoToneMapping, OrthographicCamera, Scene, Spherical, SRGBColorSpace, Vector3, WebGLRenderer } from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { createHomeModelControls } from './home-model-controls.js';

function disposeModel(root) {
  const resources = new Set();
  root.traverse(object => {
    if (object.geometry) resources.add(object.geometry);
    for (const material of [object.material].flat().filter(Boolean)) {
      resources.add(material);
      for (const value of Object.values(material)) if (value?.isTexture) resources.add(value);
    }
  });
  for (const resource of resources) resource.dispose();
}

class HomeModel extends HTMLElement {
  connectedCallback() {
    const controller = this.controller = new AbortController();
    this.initialize(controller.signal).catch(error => {
      if (controller.signal.aborted) return;
      console.error('Home model could not be loaded.', error);
      this.cleanup?.();
    });
  }

  disconnectedCallback() {
    this.controller?.abort();
    this.cleanup?.();
  }

  async initialize(signal) {
    const gltf = await new GLTFLoader().loadAsync('/models/exhyperv-icon.glb');
    if (signal.aborted) { disposeModel(gltf.scene); return; }
    const model = gltf.scene;
    const scene = new Scene();
    scene.add(model);
    const center = new Box3().setFromObject(model).getCenter(new Vector3());
    const camera = gltf.cameras[0] || new OrthographicCamera(-4, 4, 4, -4, 0.01, 100);
    if (!gltf.cameras.length) camera.position.set(6, 6, 6);
    scene.attach(camera);
    camera.updateMatrixWorld();

    let renderer;
    try {
      renderer = new WebGLRenderer({ alpha: true, antialias: true, powerPreference: 'low-power' });
    } catch (error) { disposeModel(model); throw error; }
    renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    renderer.outputColorSpace = SRGBColorSpace;
    renderer.toneMapping = NoToneMapping;
    renderer.setClearColor(0, 0);
    renderer.domElement.setAttribute('aria-hidden', 'true');
    this.append(renderer.domElement);

    const controls = createHomeModelControls(camera, renderer.domElement, center);

    const render = () => renderer.render(scene, camera);
    controls.addEventListener('change', render);
    controls.addEventListener('start', () => this.setAttribute('data-dragging', ''));
    controls.addEventListener('end', () => this.removeAttribute('data-dragging'));
    this.addEventListener('dblclick', () => controls.reset(), { signal });
    this.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home'].includes(event.key)) return;
      event.preventDefault();
      if (event.key === 'Home') { controls.reset(); return; }
      const offset = camera.position.clone().sub(controls.target);
      const spherical = new Spherical().setFromVector3(offset);
      if (event.key === 'ArrowLeft') spherical.theta += 0.12;
      if (event.key === 'ArrowRight') spherical.theta -= 0.12;
      if (event.key === 'ArrowUp') spherical.phi -= 0.12;
      if (event.key === 'ArrowDown') spherical.phi += 0.12;
      spherical.phi = Math.max(controls.minPolarAngle, Math.min(controls.maxPolarAngle, spherical.phi));
      camera.position.copy(controls.target).add(offset.setFromSpherical(spherical));
      controls.update();
    }, { signal });
    renderer.domElement.addEventListener('webglcontextlost', event => {
      event.preventDefault();
      this.removeAttribute('data-ready');
    }, { signal });
    renderer.domElement.addEventListener('webglcontextrestored', () => {
      render();
      this.setAttribute('data-ready', '');
    }, { signal });
    const resize = new ResizeObserver(() => {
      const width = this.clientWidth;
      if (!width) return;
      renderer.setSize(width, width, false);
      render();
    });
    this.cleanup = () => {
      this.cleanup = undefined;
      resize.disconnect();
      controls.dispose();
      disposeModel(model);
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
      this.removeAttribute('data-ready');
      this.removeAttribute('data-dragging');
    };
    resize.observe(this);
    renderer.setSize(this.clientWidth || 360, this.clientWidth || 360, false);
    render();
    this.setAttribute('data-ready', '');
  }
}

if (!customElements.get('home-model')) customElements.define('home-model', HomeModel);
