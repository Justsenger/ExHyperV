import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { createConcept2Viewer, createStageTextures } from './concepts-3d.js';

function disposeGltfTextures(root) {
  const textures = new Set();
  root.traverse((object) => {
    for (const material of [object.material].flat().filter(Boolean)) {
      for (const value of Object.values(material)) {
        if (value?.isTexture) textures.add(value);
      }
    }
  });
  for (const texture of textures) texture.dispose();
}

class HomeModel extends HTMLElement {
  connectedCallback() {
    const controller = this.controller = new AbortController();
    this.initialize(controller.signal).catch((error) => {
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
    const darkMq = window.matchMedia('(prefers-color-scheme: dark)');
    const motionMq = window.matchMedia('(prefers-reduced-motion: reduce)');
    const state = {
      theme: darkMq.matches ? 'dark' : 'light',
      motion: motionMq.matches ? 'static' : 'combo',
      surroundMode: this.dataset.surround || 'updraft-velvet',
      globalLocked: false,
      fxGlow: true,
      fxConduits: true,
      fxShards: true,
      fxSpring: true,
      fxFieldRing: false,
      faceStyle: 'lit-satin',
    };

    const syncTheme = () => { state.theme = darkMq.matches ? 'dark' : 'light'; };
    const syncMotion = () => { state.motion = motionMq.matches ? 'static' : 'combo'; };
    darkMq.addEventListener('change', syncTheme, { signal });
    motionMq.addEventListener('change', syncMotion, { signal });

    const gltf = await new GLTFLoader().loadAsync('/models/exhyperv-icon.glb');
    if (signal.aborted) {
      disposeGltfTextures(gltf.scene);
      return;
    }

    const stageAssets = createStageTextures();
    const viewer = await createConcept2Viewer({
      stageEl: this,
      hostEl: this,
      coreStyle: 'quad-turbine',
      powerPreference: 'low-power',
      gltfSource: gltf,
      ...stageAssets,
      stateGetter: () => state,
    });

    if (signal.aborted || !viewer) {
      viewer?.destroy();
      stageAssets.dispose();
      disposeGltfTextures(gltf.scene);
      return;
    }

    this.addEventListener('c2-lock-changed', () => {
      this.toggleAttribute('data-locked', viewer.isLocked());
    }, { signal });

    let last = performance.now();
    let elapsed = 0;
    let rafId = 0;

    const tick = (now) => {
      if (signal.aborted) return;
      const dt = Math.min(0.05, Math.max(0.001, (now - last) / 1000));
      last = now;
      elapsed += dt;
      viewer.update(dt, elapsed);
      rafId = requestAnimationFrame(tick);
    };
    rafId = requestAnimationFrame(tick);

    this.cleanup = () => {
      this.cleanup = undefined;
      cancelAnimationFrame(rafId);
      viewer.destroy();
      stageAssets.dispose();
      disposeGltfTextures(gltf.scene);
      this.removeAttribute('data-locked');
    };
  }
}

if (!customElements.get('home-model')) customElements.define('home-model', HomeModel);
