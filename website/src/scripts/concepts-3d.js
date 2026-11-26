import {
  AdditiveBlending,
  AmbientLight,
  Box3,
  BufferGeometry,
  Color,
  DirectionalLight,
  DoubleSide,
  EdgesGeometry,
  Float32BufferAttribute,
  Group,
  LatheGeometry,
  LineBasicMaterial,
  LineSegments,
  Matrix3,
  Mesh,
  MeshBasicMaterial,
  MeshPhysicalMaterial,
  NormalBlending,
  NoToneMapping,
  OctahedronGeometry,
  OrthographicCamera,
  PlaneGeometry,
  PMREMGenerator,
  Quaternion,
  Scene,
  ShaderMaterial,
  Spherical,
  Sprite,
  SpriteMaterial,
  SRGBColorSpace,
  TorusGeometry,
  Vector2,
  Vector3,
  WebGLRenderer,
} from 'three';
import { createHomeModelControls } from './home-model-controls.js';
import {
  createArchitecturalStageTexture,
  createContactShadowTexture,
  createHoloReticleTexture,
  createHorizonMatrixTexture,
  createOpticalFlareTexture,
  createOsIconPack,
  createPulseRippleTexture,
  createRadialGlowTexture,
} from './vm-textures.js';

function disposeObjectTree(root) {
  const geometries = new Set();
  const materials = new Set();
  root.traverse((obj) => {
    if (obj.geometry) geometries.add(obj.geometry);
    for (const mat of [obj.material].flat().filter(Boolean)) {
      materials.add(mat);
    }
  });
  for (const g of geometries) g.dispose();
  for (const m of materials) m.dispose();
}

function createStudioEnvTexture(renderer) {
  const envScene = new Scene();
  envScene.background = new Color('#081322');

  const panelGeo = new PlaneGeometry(10, 10);
  const panels = [
    { pos: [0, 8, 2], look: [0, 0, 0], color: '#e6f7ff', scale: [1.5, 1.5, 1] },
    { pos: [8, 3, 6], look: [0, 0, 0], color: '#6fd6ff', scale: [1.2, 1.4, 1] },
    { pos: [-8, 4, -5], look: [0, 0, 0], color: '#1b88ff', scale: [1.3, 1.2, 1] },
    { pos: [0, -7, 4], look: [0, 0, 0], color: '#0c3f8a', scale: [1.6, 1.6, 1] },
  ];
  for (const cfg of panels) {
    const mat = new MeshBasicMaterial({ color: cfg.color, side: DoubleSide });
    const mesh = new Mesh(panelGeo, mat);
    mesh.position.set(...cfg.pos);
    mesh.scale.set(...cfg.scale);
    mesh.lookAt(...cfg.look);
    envScene.add(mesh);
  }

  const pmrem = new PMREMGenerator(renderer);
  const envTex = pmrem.fromScene(envScene, 0.04).texture;
  pmrem.dispose();
  disposeObjectTree(envScene);
  return envTex;
}

function buildRazorBladePedestal({ envTex, shadowTex }) {
  const deckMat = new MeshPhysicalMaterial({
    color: '#131d2d',
    emissive: '#050c18',
    emissiveIntensity: 0.18,
    roughness: 0.42,
    metalness: 0.44,
    clearcoat: 0.42,
    clearcoatRoughness: 0.28,
    envMap: envTex,
    envMapIntensity: 1.55,
    transparent: true,
    opacity: 0,
  });
  const bevelMat = new MeshPhysicalMaterial({
    color: '#203048',
    emissive: '#08182e',
    emissiveIntensity: 0.22,
    roughness: 0.28,
    metalness: 0.62,
    clearcoat: 0.72,
    clearcoatRoughness: 0.18,
    envMap: envTex,
    envMapIntensity: 1.9,
    transparent: true,
    opacity: 0,
  });
  const grooveMat = new MeshBasicMaterial({
    color: '#5ad2ff',
    transparent: true,
    opacity: 0,
    toneMapped: false,
  });
  const rimLineMat = new LineBasicMaterial({
    color: '#8ce2ff',
    transparent: true,
    opacity: 0,
    depthWrite: false,
  });
  const floorDropMat = new MeshBasicMaterial({
    map: shadowTex,
    color: '#02060e',
    transparent: true,
    opacity: 0,
    depthWrite: false,
    toneMapped: false,
  });

  const toVecs = (pts) => pts.map(([r, y]) => new Vector2(r, y));
  // Wide satin concave deck (y: -0.024 -> -0.004)
  const deckGeo = new LatheGeometry(
    toVecs([[0, -0.022], [3.34, -0.022], [3.42, -0.004], [3.42, -0.095], [0, -0.095]]),
    64,
  );
  // Wide razor knife-edge chamfer skirt (r = 3.42 -> 3.74) & tapered under-foot
  const skirtGeo = new LatheGeometry(
    toVecs([[3.42, -0.004], [3.74, -0.095], [3.54, -0.105], [3.50, -0.225], [2.78, -0.240], [0, -0.240], [0, -0.125], [3.40, -0.125], [3.42, -0.004]]),
    64,
  );
  // Hairline inset optical light channel tucked cleanly along the chamfer waist
  const grooveGeo = new LatheGeometry(
    toVecs([[3.46, -0.118], [3.55, -0.118], [3.55, -0.098], [3.46, -0.098]]),
    64,
  );

  const deckMesh = new Mesh(deckGeo, deckMat);
  const skirtMesh = new Mesh(skirtGeo, bevelMat);
  const grooveMesh = new Mesh(grooveGeo, grooveMat);
  const rimLines = new LineSegments(new EdgesGeometry(skirtGeo, 24), rimLineMat);

  const floorDrop = new Mesh(new PlaneGeometry(9.8, 9.8), floorDropMat);
  floorDrop.rotation.x = -Math.PI / 2;
  floorDrop.position.y = -0.255;
  floorDrop.renderOrder = -8;

  const holder = new Group();
  holder.add(floorDrop, grooveMesh, deckMesh, skirtMesh, rimLines);

  return {
    holder,
    update(alpha, vis, pulseBoost, isDark) {
      const solid = alpha > 0.985;
      deckMat.color.set(isDark ? '#131d2d' : '#dde8f5');
      deckMat.emissive.set(isDark ? '#050c18' : '#a7c6ea');
      deckMat.emissiveIntensity = isDark ? 0.18 : 0.12;
      deckMat.roughness = isDark ? 0.42 : 0.30;
      deckMat.metalness = isDark ? 0.44 : 0.24;
      deckMat.transparent = !solid;
      deckMat.depthWrite = solid;
      deckMat.opacity = alpha;

      bevelMat.color.set(isDark ? '#203048' : '#c4d8ee');
      bevelMat.emissive.set(isDark ? '#08182e' : '#85aee0');
      bevelMat.emissiveIntensity = isDark ? 0.22 : 0.14;
      bevelMat.transparent = !solid;
      bevelMat.depthWrite = solid;
      bevelMat.opacity = alpha;

      grooveMat.color.set(isDark ? '#66e4ff' : '#0072e5');
      grooveMat.opacity = alpha * Math.min(1, (isDark ? 0.48 : 0.56) + vis * 0.36 + pulseBoost * 0.32);

      rimLineMat.color.set(isDark ? '#8ee4ff' : '#3b82f6');
      rimLineMat.opacity = alpha * (isDark ? 0.42 + vis * 0.22 : 0.36);

      floorDropMat.color.set(isDark ? '#02060e' : '#12386e');
      floorDropMat.opacity = alpha * (isDark ? 0.58 : 0.16);
    },
  };
}

/**
 * 3D Hero stage rig comparing 4 zero-snap spatial directions:
 * - 'atelier-ambient': Borderless studio glow + feathered micro-dot coordinate floor
 * - 'reveal-holofield': Strictly clean at rest (0 snap); dynamic dual holographic reticle rings bloom on explode
 * - 'fluent-mica-panel': Clean floating shadows landing on the underlying CSS WinUI 3 Mica glass surface
 * - 'razor-obsidian-pedestal': Ultra-wide, ultra-thin knife-edge blade dais (with paired static poster plan)
 */
function createStageFloorRig({
  bgStyle = 'default',
  envTex,
  stageTex,
  shadowTex,
  glowTex,
  rippleTex,
  horizonTex,
  holoTex,
  lowerCells,
}) {
  const FLOOR_Y = -3.18;
  const group = new Group();
  group.position.y = FLOOR_Y;

  const isHolofield = bgStyle === 'reveal-holofield';
  const isMicaPanel = bgStyle === 'fluent-mica-panel';
  const isAtelier = bgStyle === 'atelier-ambient';
  const isRazor = bgStyle === 'razor-obsidian-pedestal';

  const baseMat = new MeshBasicMaterial({
    map: stageTex,
    color: '#6ec8ff',
    transparent: true,
    opacity: 0,
    depthWrite: false,
    toneMapped: false,
  });
  const basePlate = new Mesh(new PlaneGeometry(7.2, 7.2), baseMat);
  basePlate.rotation.x = -Math.PI / 2;
  basePlate.renderOrder = -4;
  group.add(basePlate);

  // Direction 1: Borderless outer-feathered spatial micro-matrix
  let atelierRig = null;
  if (isAtelier && horizonTex) {
    const mat = new MeshBasicMaterial({
      map: horizonTex,
      color: '#5cc8ff',
      transparent: true,
      opacity: 0.24,
      depthWrite: false,
      toneMapped: false,
    });
    const mesh = new Mesh(new PlaneGeometry(12.2, 12.2), mat);
    mesh.rotation.x = -Math.PI / 2;
    mesh.position.y = -0.004;
    mesh.renderOrder = -5;
    group.add(mesh);
    atelierRig = { mat };
  }

  // Direction 2: Dual counter-rotating holographic reticle rings (hidden when closed)
  let holoRig = null;
  if (isHolofield && holoTex) {
    const outerMat = new MeshBasicMaterial({
      map: holoTex,
      color: '#6ce0ff',
      transparent: true,
      opacity: 0,
      blending: AdditiveBlending,
      depthWrite: false,
      toneMapped: false,
    });
    const innerMat = outerMat.clone();

    const outerMesh = new Mesh(new PlaneGeometry(8.6, 8.6), outerMat);
    outerMesh.rotation.x = -Math.PI / 2;
    outerMesh.position.y = 0.002;
    outerMesh.renderOrder = -4;

    const innerMesh = new Mesh(new PlaneGeometry(5.6, 5.6), innerMat);
    innerMesh.rotation.x = -Math.PI / 2;
    innerMesh.position.y = 0.006;
    innerMesh.renderOrder = -3;

    group.add(outerMesh, innerMesh);
    holoRig = { outerMat, innerMat, outerMesh, innerMesh };
  }

  // Direction 4: Ultra-wide razor knife-edge obsidian/titanium blade dais
  const razorRig = isRazor ? buildRazorBladePedestal({ envTex, shadowTex }) : null;
  if (razorRig) group.add(razorRig.holder);

  const coreBounceMat = new MeshBasicMaterial({
    map: glowTex,
    color: '#52c8ff',
    transparent: true,
    opacity: 0,
    blending: AdditiveBlending,
    depthWrite: false,
    toneMapped: false,
  });
  const coreBounce = new Mesh(new PlaneGeometry(4.0, 4.0), coreBounceMat);
  coreBounce.rotation.x = -Math.PI / 2;
  coreBounce.position.y = 0.012;
  coreBounce.renderOrder = -3;
  group.add(coreBounce);

  const shadowGeo = new PlaneGeometry(1, 1);
  const shadowItems = lowerCells.map((cell) => {
    const mat = new MeshBasicMaterial({
      map: shadowTex,
      color: '#040914',
      transparent: true,
      opacity: 0.44,
      depthWrite: false,
      toneMapped: false,
    });
    const mesh = new Mesh(shadowGeo, mat);
    mesh.rotation.x = -Math.PI / 2;
    mesh.position.y = 0.008;
    mesh.renderOrder = -2;
    group.add(mesh);
    return { cell, mesh, mat };
  });

  const rippleGeo = new PlaneGeometry(1, 1);
  const activeRipples = [];
  const tmpPos = new Vector3();

  return {
    group,
    triggerPulse() {
      activeRipples.push({ progress: 0 });
    },
    update(dt, elapsed, cameraPos, pivotY, clampExp, vis, pulseBoost, isDark, showGlow) {
      const camLen = cameraPos.length() || 1;
      const camDirY = cameraPos.y / camLen;
      // Smoothly vanish at grazing/underside angles so underside orbit inspection stays unobstructed
      const t = Math.max(0, Math.min(1, (camDirY - 0.08) / 0.30));
      const angleVis = t * t * (3 - 2 * t);

      group.visible = angleVis > 0.01;
      if (!group.visible) return;

      const holoOpenVis = isHolofield ? Math.max(0, Math.min(1, clampExp / 0.25)) : 1;

      baseMat.color.set(isDark ? '#6ec8ff' : '#2a6bb8');
      const baseMul =
        isHolofield ? vis * 0.55 :
        isAtelier ? 0.52 :
        isMicaPanel ? 0.34 :
        isRazor ? 0.24 : vis * 0.45;
      baseMat.opacity = angleVis * baseMul * (0.28 + vis * 0.72) * (isDark ? 0.42 : 0.26);

      if (atelierRig) {
        atelierRig.mat.color.set(isDark ? '#58c6ff' : '#2563eb');
        atelierRig.mat.opacity = angleVis * (isDark ? 0.22 + vis * 0.16 : 0.16 + vis * 0.10);
      }

      if (holoRig) {
        const hAlpha = angleVis * vis;
        holoRig.outerMesh.visible = hAlpha > 0.01;
        holoRig.innerMesh.visible = hAlpha > 0.01;
        const hScale = 0.62 + vis * 0.38;
        holoRig.outerMesh.scale.set(hScale, hScale, 1);
        holoRig.innerMesh.scale.set(hScale, hScale, 1);
        holoRig.outerMesh.rotation.z = elapsed * 0.14;
        holoRig.innerMesh.rotation.z = -elapsed * 0.26;
        holoRig.outerMat.blending = isDark ? AdditiveBlending : NormalBlending;
        holoRig.innerMat.blending = isDark ? AdditiveBlending : NormalBlending;
        holoRig.outerMat.color.set(isDark ? '#6ae0ff' : '#0062ba');
        holoRig.innerMat.color.set(isDark ? '#aaf0ff' : '#0284c7');
        holoRig.outerMat.opacity = hAlpha * (isDark ? 0.72 + pulseBoost * 0.24 : 0.54 + pulseBoost * 0.2);
        holoRig.innerMat.opacity = hAlpha * (isDark ? 0.64 + pulseBoost * 0.28 : 0.48 + pulseBoost * 0.2);
      }

      if (razorRig) {
        // Gentle initial fade avoids harsh pop over the static poster
        const razorIntro = Math.min(1, elapsed / 0.38);
        razorRig.update(angleVis * razorIntro, vis, pulseBoost, isDark);
      }

      for (const s of shadowItems) {
        s.cell.obj.getWorldPosition(tmpPos);
        const gap = Math.max(0.15, tmpPos.y - FLOOR_Y);
        const spread = 2.15 + gap * 0.42 + pivotY * 0.12;
        const heightAlpha = Math.max(0.45, Math.min(1.0, 1.15 - gap * 0.28));
        s.mesh.position.set(tmpPos.x, 0.008, tmpPos.z);
        s.mesh.scale.set(spread, spread, 1);
        s.mat.color.set(isDark ? '#030812' : '#113a6e');
        const shadowMul = isHolofield
          ? holoOpenVis * (isDark ? 0.44 : 0.16)
          : isMicaPanel
            ? (isDark ? 0.38 : 0.15)
            : isRazor
              ? (isDark ? 0.48 : 0.16)
              : isAtelier
                ? (isDark ? 0.50 : 0.18)
                : vis * (isDark ? 0.44 : 0.16);
        s.mat.opacity = angleVis * heightAlpha * shadowMul;
      }

      coreBounceMat.blending = isDark ? AdditiveBlending : NormalBlending;
      coreBounceMat.color.set(isDark ? '#58d0ff' : '#38bdf8');
      const bounceAlpha = showGlow
        ? angleVis * holoOpenVis * (vis * (isDark ? 0.28 : 0.08) + pulseBoost * (isDark ? 0.24 : 0.07))
        : 0;
      coreBounce.visible = bounceAlpha > 0.005;
      coreBounceMat.opacity = bounceAlpha;
      const bScale = 0.86 + vis * 0.36 + pulseBoost * 0.28;
      coreBounce.scale.set(bScale, bScale, 1);

      for (let i = activeRipples.length - 1; i >= 0; i -= 1) {
        const rp = activeRipples[i];
        rp.progress += dt * 1.15;
        if (rp.progress >= 1) {
          if (rp.mesh) {
            group.remove(rp.mesh);
            rp.mat.dispose();
          }
          activeRipples.splice(i, 1);
          continue;
        }
        if (!rp.mesh) {
          rp.mat = new MeshBasicMaterial({
            map: rippleTex,
            color: '#6ce0ff',
            transparent: true,
            opacity: 0,
            blending: isDark ? AdditiveBlending : NormalBlending,
            depthWrite: false,
            toneMapped: false,
          });
          rp.mesh = new Mesh(rippleGeo, rp.mat);
          rp.mesh.rotation.x = -Math.PI / 2;
          rp.mesh.position.y = 0.016;
          rp.mesh.renderOrder = -1;
          group.add(rp.mesh);
        }
        const ease = 1 - Math.pow(1 - rp.progress, 3);
        const span = 1.8 + ease * 6.0;
        rp.mesh.scale.set(span, span, 1);
        rp.mat.blending = isDark ? AdditiveBlending : NormalBlending;
        rp.mat.color.set(isDark ? '#7ee4ff' : '#0072e5');
        rp.mat.opacity = angleVis * (1 - rp.progress) * (isDark ? 0.58 : 0.34);
      }
    },
  };
}

/**
 * Builds the 3 Concept 2 Core directions comparing No-Solid-Core vs Pure Relic Crystal vs Quadrant Conduit Ring.
 */
function buildCoreVariant(coreStyle, { envTex, flareTex, camera }) {
  const root = new Group();

  const innerCoreMat = new MeshBasicMaterial({
    color: '#f2fcff',
    transparent: true,
    opacity: 0.98,
    toneMapped: false,
  });
  const shellGlassMat = new MeshPhysicalMaterial({
    color: '#6ce0ff',
    emissive: '#0c72e8',
    emissiveIntensity: 0.52,
    roughness: 0.04,
    metalness: 0.02,
    clearcoat: 1.0,
    clearcoatRoughness: 0.04,
    transparent: true,
    opacity: 0.48,
    envMap: envTex,
    envMapIntensity: 2.3,
    depthWrite: false,
  });
  const wireMat = new LineBasicMaterial({
    color: '#d4f6ff',
    transparent: true,
    opacity: 0.56,
    depthWrite: false,
  });
  const ringMatA = new MeshBasicMaterial({
    color: '#76deff',
    transparent: true,
    opacity: 0.9,
    depthWrite: false,
    toneMapped: false,
  });
  const ringMatB = new MeshBasicMaterial({
    color: '#ccf5ff',
    transparent: true,
    opacity: 0.68,
    depthWrite: false,
    toneMapped: false,
  });
  const flareMat = new SpriteMaterial({
    map: flareTex,
    color: '#ffffff',
    transparent: true,
    opacity: 0,
    blending: AdditiveBlending,
    depthWrite: false,
    depthTest: true,
  });

  let updateCore = () => {};

  if (coreStyle === 'void-nexus') {
    // 无实体核心：纯能量奇点闪耀 + 4向星芒耀斑 + 连续脉冲扩散光波
    const spark = new Mesh(new OctahedronGeometry(0.15, 0), innerCoreMat);
    spark.scale.set(0.85, 1.35, 0.85);
    const flareSprite = new Sprite(flareMat);
    flareSprite.renderOrder = 2;

    const waveGeo = new TorusGeometry(1.0, 0.014, 8, 64);
    const waveMatA = ringMatA.clone();
    const waveMatB = ringMatB.clone();
    const waveA = new Mesh(waveGeo, waveMatA);
    const waveB = new Mesh(waveGeo, waveMatB);

    root.add(spark, flareSprite, waveA, waveB);

    updateCore = (elapsed, vis, pulseBoost, isDark) => {
      spark.rotation.y = elapsed * 1.4;
      spark.scale.setScalar(0.85 + Math.sin(elapsed * 4.2) * 0.14 + pulseBoost * 0.45);

      flareMat.blending = isDark ? AdditiveBlending : NormalBlending;
      flareMat.color.set(isDark ? '#e6faff' : '#0072e5');
      flareMat.opacity = vis * (isDark ? 0.95 : 0.72);
      const flareScale = 1.9 + vis * 1.3 + Math.sin(elapsed * 2.8) * 0.16 + pulseBoost * 0.9;
      flareSprite.scale.set(flareScale, flareScale, 1);

      const updateWave = (waveMesh, waveMat, phaseOffset) => {
        const w = (elapsed * (0.48 + pulseBoost * 0.5) + phaseOffset) % 1;
        waveMesh.lookAt(camera.position);
        waveMesh.scale.setScalar(0.16 + w * 1.05);
        waveMat.color.set(isDark ? '#8ae5ff' : '#0072e5');
        waveMat.opacity = vis * Math.sin(w * Math.PI) * (isDark ? 0.68 : 0.54);
      };
      updateWave(waveA, waveMatA, 0);
      updateWave(waveB, waveMatB, 0.5);
    };
  } else if (coreStyle === 'relic-gem') {
    // 圣物原石：与外圈8蓝晶同宗同源的极纯白青切割双锥光钻（无金属无黑漆）
    const gemInner = new Mesh(new OctahedronGeometry(0.45, 0), innerCoreMat);
    gemInner.scale.set(0.76, 1.42, 0.76);

    const gemOuterGeo = new OctahedronGeometry(0.76, 0);
    const gemOuter = new Mesh(gemOuterGeo, shellGlassMat);
    gemOuter.scale.set(0.86, 1.50, 0.86);

    const gemEdges = new LineSegments(new EdgesGeometry(gemOuterGeo, 1), wireMat);
    gemEdges.scale.copy(gemOuter.scale);

    const eqHalo = new Mesh(new TorusGeometry(1.02, 0.013, 10, 64), ringMatB);
    eqHalo.rotation.set(Math.PI * 0.36, 0.25, 0.2);

    const flareSprite = new Sprite(flareMat);
    flareSprite.renderOrder = 1;

    root.add(flareSprite, gemInner, gemOuter, gemEdges, eqHalo);

    updateCore = (elapsed, vis, pulseBoost, isDark) => {
      const spinMult = 1 + pulseBoost * 1.8;
      gemInner.rotation.y = elapsed * 0.95 * spinMult;
      gemOuter.rotation.y = -elapsed * 0.52 * spinMult;
      gemEdges.rotation.y = gemOuter.rotation.y;

      const p = 1 + Math.sin(elapsed * 2.8) * 0.055 + pulseBoost * 0.14;
      gemInner.scale.set(0.76 * p, 1.42 * p, 0.76 * p);

      eqHalo.rotation.z = elapsed * 0.65;
      eqHalo.rotation.x = Math.PI * 0.36 + Math.sin(elapsed * 0.6) * 0.14;

      flareMat.blending = isDark ? AdditiveBlending : NormalBlending;
      flareMat.color.set(isDark ? '#daf7ff' : '#0072e5');
      flareMat.opacity = vis * (isDark ? 0.82 : 0.45);
      const flareScale = 1.6 + vis * 1.15 + pulseBoost * 0.7;
      flareSprite.scale.set(flareScale, flareScale, 1);
    };
  } else {
    // 四象限光弧中枢（定案）：晶钻微核 + 四象限玻璃导流光弧与节点 + 内旋细环
    const spark = new Mesh(new OctahedronGeometry(0.25, 0), innerCoreMat);
    spark.scale.set(0.84, 1.36, 0.84);
    const sparkShellGeo = new OctahedronGeometry(0.41, 0);
    const sparkShell = new Mesh(sparkShellGeo, shellGlassMat);
    sparkShell.scale.set(0.90, 1.40, 0.90);
    const sparkEdges = new LineSegments(new EdgesGeometry(sparkShellGeo, 1), wireMat);
    sparkEdges.scale.copy(sparkShell.scale);

    const flareSprite = new Sprite(flareMat);
    flareSprite.renderOrder = 1;

    const quadGroup = new Group();
    quadGroup.rotation.set(0.18, 0, -0.14);
    const arcRadius = 0.84;
    const arcSpan = Math.PI * 0.34;
    const arcGlassGeo = new TorusGeometry(arcRadius, 0.042, 12, 28, arcSpan);
    const arcTrimGeo = new TorusGeometry(arcRadius, 0.013, 8, 28, Math.PI * 0.38);
    const nodeGeo = new OctahedronGeometry(0.056, 0);

    for (let i = 0; i < 4; i += 1) {
      const blade = new Group();
      blade.rotation.x = Math.PI / 2;
      blade.rotation.z = (i * Math.PI) / 2;
      const gMesh = new Mesh(arcGlassGeo, shellGlassMat);
      const tMesh = new Mesh(arcTrimGeo, ringMatA);
      tMesh.rotation.z = -0.02;
      const midAng = arcSpan * 0.5;
      const node = new Mesh(nodeGeo, innerCoreMat);
      node.position.set(Math.cos(midAng) * arcRadius, Math.sin(midAng) * arcRadius, 0);
      blade.add(gMesh, tMesh, node);
      quadGroup.add(blade);
    }

    const innerRing = new Mesh(new TorusGeometry(0.56, 0.013, 8, 56), ringMatB);
    innerRing.rotation.x = Math.PI * 0.44;

    root.add(flareSprite, spark, sparkShell, sparkEdges, quadGroup, innerRing);

    updateCore = (elapsed, vis, pulseBoost, isDark) => {
      const spinMult = 1 + pulseBoost * 1.4;
      spark.rotation.y = elapsed * 1.25 * spinMult;
      sparkShell.rotation.y = -elapsed * 0.72 * spinMult;
      sparkEdges.rotation.y = sparkShell.rotation.y;
      const pulseScale = 1 + Math.sin(elapsed * 3.0) * 0.07 + pulseBoost * 0.20;
      spark.scale.set(0.84 * pulseScale, 1.36 * pulseScale, 0.84 * pulseScale);

      quadGroup.rotation.y = elapsed * (0.56 + pulseBoost * 0.85);
      innerRing.rotation.z = -elapsed * 0.9;
      innerRing.rotation.y = elapsed * 0.4;

      flareMat.blending = isDark ? AdditiveBlending : NormalBlending;
      flareMat.color.set(isDark ? '#daf7ff' : '#0072e5');
      flareMat.opacity = vis * (isDark ? 0.76 : 0.44);
      const flareScale = 1.35 + vis * 1.0 + pulseBoost * 0.65;
      flareSprite.scale.set(flareScale, flareScale, 1);
    };
  }

  return {
    root,
    update(elapsed, vis, pulseBoost, isDark) {
      root.visible = vis > 0.015;
      if (!root.visible) return;
      const scale = 0.22 + vis * 0.78;
      root.scale.setScalar(scale);

      innerCoreMat.color.set(isDark ? '#f2fcff' : '#0072e5');
      innerCoreMat.opacity = 0.38 + vis * 0.6;
      shellGlassMat.color.set(isDark ? '#6ce0ff' : '#0ea5e9');
      shellGlassMat.emissive.set(isDark ? '#0c72e8' : '#0369a1');
      shellGlassMat.emissiveIntensity = (isDark ? 0.55 : 0.36) + pulseBoost * 0.5;
      shellGlassMat.opacity = vis * (isDark ? 0.48 : 0.62);
      wireMat.color.set(isDark ? '#d4f6ff' : '#0056b3');
      wireMat.opacity = vis * (isDark ? 0.52 : 0.64);
      ringMatA.color.set(isDark ? '#76deff' : '#0072e5');
      ringMatA.opacity = vis * 0.9;
      ringMatB.color.set(isDark ? '#ccf5ff' : '#0284c7');
      ringMatB.opacity = vis * 0.68;

      updateCore(elapsed, vis, pulseBoost, isDark);
    },
  };
}

export function createStageTextures() {
  const osPack = createOsIconPack();
  const glowTex = createRadialGlowTexture(128);
  const flareTex = createOpticalFlareTexture(256);
  const stageTex = createArchitecturalStageTexture(1024);
  const shadowTex = createContactShadowTexture(256);
  const rippleTex = createPulseRippleTexture(256);
  const horizonTex = createHorizonMatrixTexture(1024);
  const holoTex = createHoloReticleTexture(1024);
  return {
    osPack,
    glowTex,
    flareTex,
    stageTex,
    shadowTex,
    rippleTex,
    horizonTex,
    holoTex,
    dispose() {
      osPack.dispose();
      glowTex.dispose();
      flareTex.dispose();
      stageTex.dispose();
      shadowTex.dispose();
      rippleTex.dispose();
      horizonTex.dispose();
      holoTex.dispose();
    },
  };
}

function createLitSatinMaterial(geom, origMat, cellSign, sharedUniforms) {
  geom.computeBoundingBox();
  const box = geom.boundingBox;
  const boxCenter = new Vector3();
  const boxSize = new Vector3();
  box.getCenter(boxCenter);
  box.getSize(boxSize);
  const boxHalf = boxSize.multiplyScalar(0.5).max(new Vector3(0.001, 0.001, 0.001));

  return new ShaderMaterial({
    side: DoubleSide,
    uniforms: {
      uBoxCenter: { value: boxCenter },
      uBoxHalf: { value: boxHalf },
      uCellSign: { value: cellSign.clone() },
      uExplode: sharedUniforms.uExplode,
      uClusterWorldPos: sharedUniforms.uClusterWorldPos,
      uRestTopDir: sharedUniforms.uRestTopDir,
    },
    vertexShader: /* glsl */ `
      uniform vec3 uBoxCenter;
      uniform vec3 uBoxHalf;
      uniform vec3 uClusterWorldPos;
      varying vec3 vViewNormal;
      varying vec3 vLocalNormal;
      varying vec3 vUnitPos;
      varying vec3 vLocalViewDir;
      varying float vCellScreenH;
      varying float vCellDepth;
      void main() {
        vLocalNormal = normalize(normal);
        vViewNormal = normalize(normalMatrix * normal);
        vUnitPos = (position - uBoxCenter) / uBoxHalf;
        // Exact local-space view sightline (transpose of orthogonal rotation rows)
        vLocalViewDir = normalize(vec3(
          modelViewMatrix[0][2],
          modelViewMatrix[1][2],
          modelViewMatrix[2][2]
        ));
        vec3 cellWorld = (modelMatrix * vec4(uBoxCenter, 1.0)).xyz;
        vec3 cellView = (viewMatrix * vec4(cellWorld, 1.0)).xyz;
        vec3 clusterView = (viewMatrix * vec4(uClusterWorldPos, 1.0)).xyz;
        vec3 cellRelView = cellView - clusterView;
        vCellScreenH = cellRelView.y;
        vCellDepth = cellRelView.z;
        vec4 mvPos = modelViewMatrix * vec4(position, 1.0);
        gl_Position = projectionMatrix * mvPos;
      }
    `,
    fragmentShader: /* glsl */ `
      uniform vec3 uCellSign;
      uniform float uExplode;
      uniform vec3 uRestTopDir;
      varying vec3 vViewNormal;
      varying vec3 vLocalNormal;
      varying vec3 vUnitPos;
      varying vec3 vLocalViewDir;
      varying float vCellScreenH;
      varying float vCellDepth;

      void main() {
        vec3 vn = normalize(vViewNormal);
        vec3 ln = normalize(vLocalNormal);
        vec3 aln = abs(ln);
        vec3 au = abs(vUnitPos);
        vec3 viewDir = vec3(0.0, 0.0, 1.0);
        vec3 localEye = normalize(vLocalViewDir);

        // ==================== WINDOWS 11 BLOOM SCULPTURAL SATIN-COBALT SHADER ====================
        // Pure Linear-sRGB calibrated to Windows 11 Dark Bloom:
        // Zero rectangular border-frame artifacts; smooth continuous velvet gradient across every facet;
        // deep abyssal ultramarine crevices, hyper-saturated royal cobalt body, and razor-clean cyan bevel crests.
        vec3 topDir = normalize(uRestTopDir);
        float ndotRestTop = dot(vn, topDir);
        float isTrueTopFace = smoothstep(0.35, 0.78, ln.y);
        float isTrueBottomFace = smoothstep(0.35, 0.78, -ln.y);

        float isUpperScreen = smoothstep(-0.92, 0.78, vCellScreenH);
        float isForeground = smoothstep(-0.95, 0.95, vCellDepth);

        // Interior crevice walls facing the center X-channels of the 2x2x2 cluster
        float innerWall = max(
          max(clamp(-ln.x * uCellSign.x, 0.0, 1.0), clamp(-ln.y * uCellSign.y, 0.0, 1.0)),
          clamp(-ln.z * uCellSign.z, 0.0, 1.0)
        );
        // Retain sculptural crevice shadow even when exploded so rotated side views stay deeply 3D
        float openLift = mix(1.0, 0.55, smoothstep(0.0, 0.85, uExplode));
        float aoMask = innerWall * openLift;
        float gapVis = 1.0 - aoMask * 0.90;

        // Smooth continuous across-face coordinate (outer exposed crest = +1, inner crevice = 0)
        vec3 outerAxes = vUnitPos * uCellSign;
        float outerExposure = clamp((outerAxes.x + outerAxes.y * 0.9 + outerAxes.z) / 2.9, -1.0, 1.0) * 0.5 + 0.5;
        vec3 creviceAxes = max(-outerAxes, 0.0);
        // Smooth distance from inner X-slot crevice across top/side faces
        float horizCrevice = clamp(length(creviceAxes.xz) * 0.85, 0.0, 1.0);
        float sideCreviceProx = clamp(length(vec2(max(creviceAxes.x, creviceAxes.z), creviceAxes.y * 0.55)) * 0.82, 0.0, 1.0);
        float edgeCreviceAO = smoothstep(0.02, 0.88, sideCreviceProx) * openLift * (1.0 - step(0.5, innerWall) * 0.40);

        // True curved vertex-normal chamfer ridge (only lights up on actual 3D rounded bevels, never a flat-face box frame)
        float maxFaceNorm = max(aln.x, max(aln.y, aln.z));
        float realChamfer = smoothstep(0.994, 0.82, maxFaceNorm);

        // 3D Studio Directional Lighting + View-Space Spatial Gradient across each block
        vec3 keyLightDir = normalize(vec3(-0.34, 0.74, 0.58));
        vec3 rimLightDir = normalize(vec3(0.58, 0.46, 0.67));
        vec3 underShadowDir = normalize(vec3(0.28, -0.82, -0.50));

        float ndotKey = clamp(dot(vn, keyLightDir) * 0.5 + 0.5, 0.0, 1.0);
        float ndotUnder = clamp(dot(vn, underShadowDir), 0.0, 1.0);

        // Continuous diagonal velvet gradient across each block face (mimics Win11 Bloom's curved petal shading)
        float petalGrad = clamp(
          outerExposure * 0.64 + (vUnitPos.y * 0.5 + 0.5) * 0.24 - horizCrevice * 0.42 + 0.08,
          0.0,
          1.0
        );

        vec3 hKey = normalize(keyLightDir + viewDir);
        vec3 hRim = normalize(rimLightDir + viewDir);
        float specBevel = (
          pow(max(dot(vn, hKey), 0.0), 32.0) * realChamfer * 0.72 +
          pow(max(dot(vn, hRim), 0.0), 40.0) * realChamfer * 0.52
        ) * (1.0 - aoMask * 0.85);

        // True Linear-RGB Windows 11 Dark Bloom Palette (ultra-low red channel prevents chalky pastel washout):
        vec3 colAbyssFold    = vec3(0.0004, 0.0018, 0.0220); // #020620 deep abyssal fold occlusion
        vec3 colUltramarine  = vec3(0.0012, 0.0082, 0.1480); // #041566 deep ultramarine trough
        vec3 colRoyalCobalt  = vec3(0.0026, 0.0360, 0.6800); // #0834d6 signature Win11 royal cobalt
        vec3 colElectricBlue = vec3(0.0058, 0.1180, 0.9800); // #1160fc luminous cobalt-azure slope
        vec3 colSatinCyan    = vec3(0.0150, 0.3100, 1.0000); // #2098ff subsurface cyan-blue crest
        vec3 colRimHighlight = vec3(0.0420, 0.5000, 1.0000); // #3cbaff razor satin bevel catchlight

        // Top facets: continuous velvet sweep from deep ultramarine/cobalt near the inner cross to electric cyan-blue at the outer crest
        float topT = clamp(petalGrad * 0.68 + ndotKey * 0.32, 0.0, 1.0);
        vec3 topCol = mix(colUltramarine, colRoyalCobalt, smoothstep(0.02, 0.38, topT));
        topCol = mix(topCol, colElectricBlue, smoothstep(0.28, 0.74, topT));
        topCol = mix(topCol, colSatinCyan, smoothstep(0.62, 0.98, topT) * 0.82);
        topCol = mix(topCol, colUltramarine, smoothstep(0.10, 0.92, horizCrevice) * openLift * 0.72);

        // Side & vertical walls: deep ultramarine shadow folds rolling smoothly into royal cobalt & electric azure
        float sideT = clamp(
          ndotKey * 0.42 + petalGrad * 0.36 + isUpperScreen * 0.18 + isForeground * 0.08 - ndotUnder * 0.32,
          0.0,
          1.0
        );
        vec3 sideCol = mix(colUltramarine, colRoyalCobalt, smoothstep(0.08, 0.52, sideT));
        sideCol = mix(sideCol, colElectricBlue, smoothstep(0.46, 0.88, sideT) * 0.86);
        sideCol = mix(sideCol, colSatinCyan, smoothstep(0.74, 0.98, sideT) * 0.46);

        // Deep velvet fold occlusion on inner walls, crevices, and undersides
        float foldDarkness = clamp(
          max(aoMask * 0.95, edgeCreviceAO * 0.76) + isTrueBottomFace * 0.54 + (1.0 - ndotKey) * 0.24,
          0.0,
          1.0
        );
        sideCol = mix(sideCol, mix(colUltramarine, colAbyssFold, 0.74), foldDarkness * 0.88);

        float topBlend = clamp(isTrueTopFace * (0.62 + 0.38 * smoothstep(-0.1, 0.7, ndotRestTop)) * gapVis, 0.0, 1.0);
        vec3 finalCol = mix(sideCol, topCol, topBlend);

        // Subtle inner core cyan subsurface bounce when exploded
        float coreBounce = smoothstep(0.15, 0.95, uExplode) * innerWall * (1.0 - outerExposure) * 0.28;
        finalCol = mix(finalCol, colElectricBlue, coreBounce);

        // Crisp Win11 Bloom satin ridge highlight ONLY along real 3D curved chamfers on outer crests
        float outerRidgeMask = smoothstep(0.85, 0.18, horizCrevice);
        float chamferGlow = realChamfer * (0.35 + 0.65 * max(topT, sideT)) * (0.28 + 0.72 * outerRidgeMask) * (1.0 - aoMask * 0.82);
        finalCol = mix(finalCol, mix(colSatinCyan, colRimHighlight, 0.46), clamp(chamferGlow * 0.74, 0.0, 0.82));
        finalCol += colRimHighlight * specBevel * (0.35 + 0.65 * outerRidgeMask);

        gl_FragColor = vec4(finalCol, 1.0);
        #include <colorspace_fragment>
      }
    `,
  });
}

export async function createConcept2Viewer({
  stageEl,
  hostEl,
  coreStyle = 'quad-turbine',
  bgStyle = 'default',
  powerPreference = 'high-performance',
  gltfSource,
  osPack,
  glowTex,
  flareTex,
  stageTex,
  shadowTex,
  rippleTex,
  horizonTex,
  holoTex,
  stateGetter,
}) {
  const host = hostEl || stageEl?.querySelector('[data-model-host]');
  const hoverTarget = stageEl || host;
  if (!host) return null;

  const renderer = new WebGLRenderer({ alpha: true, antialias: true, powerPreference });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.outputColorSpace = SRGBColorSpace;
  renderer.toneMapping = NoToneMapping;
  renderer.setClearColor(0, 0);
  renderer.domElement.setAttribute('aria-hidden', 'true');

  const scene = new Scene();
  const pivot = new Group();
  scene.add(pivot);

  const model = gltfSource.scene.clone(true);
  const center = new Box3().setFromObject(model).getCenter(new Vector3());
  model.position.sub(center);
  pivot.add(model);

  const baseRotX = 0;
  const baseRotZ = 0;

  const srcCam = gltfSource.cameras[0] || new OrthographicCamera(-4, 4, 4, -4, 0.01, 100);
  const camera = srcCam.clone();
  const orbitTarget = new Vector3(0, 0, 0);
  camera.position.sub(center);

  // Align strictly with <img width="78%"> static icon frustum at rest (0 load snap), widening smoothly when exploded
  const srcRestExtent = camera.isOrthographicCamera ? (camera.right || 4.10) / 0.78 : 5.2564;
  const openExtent = 7.45;
  if (camera.isOrthographicCamera) {
    camera.left = -srcRestExtent;
    camera.right = srcRestExtent;
    camera.top = srcRestExtent;
    camera.bottom = -srcRestExtent;
    camera.near = 0.01;
    camera.far = 120;
    camera.updateProjectionMatrix();
  }
  scene.attach(camera);
  camera.updateMatrixWorld();

  host.append(renderer.domElement);

  const envTex = createStudioEnvTexture(renderer);
  const keyLight = new DirectionalLight('#eef8ff', 0.42);
  keyLight.position.set(-4, 11, 7);
  const fillLight = new DirectionalLight('#90dcff', 0.18);
  fillLight.position.set(9, 2, 5);
  const rimLight = new DirectionalLight('#52c8ff', 0.14);
  rimLight.position.set(4, 6, -8);
  const ambLight = new AmbientLight('#dbeeff', 0.18);
  scene.add(keyLight, fillLight, rimLight, ambLight);
  const camRight = new Vector3();
  const camUp = new Vector3();
  const camForward = new Vector3();

  const controls = createHomeModelControls(camera, renderer.domElement, orbitTarget);
  const initCamSpherical = new Spherical().setFromVector3(camera.position.clone().sub(orbitTarget));
  const curCamSpherical = new Spherical();

  const restTopDir = new Vector3(0, 1, 0)
    .applyMatrix3(new Matrix3().setFromMatrix4(camera.matrixWorldInverse))
    .normalize();
  const clusterWorldPos = new Vector3();
  const sharedLitUniforms = {
    uExplode: { value: 0 },
    uClusterWorldPos: { value: clusterWorldPos },
    uRestTopDir: { value: restTopDir },
  };
  function syncLitUniforms(curExplode = 0) {
    camera.updateMatrixWorld();
    pivot.updateWorldMatrix(true, false);
    sharedLitUniforms.uExplode.value = curExplode;
    pivot.getWorldPosition(clusterWorldPos);
  }

  // Locate all 8 Crystal cells and pair each with a texture-preserving view-lit material
  const cellTargets = [];
  const cellMaterials = [];
  const litMatCache = new Map();
  for (const child of model.children) {
    if (/^Crystal/i.test(child.name)) {
      const signX = Math.sign(child.position.x) || 1;
      const signZ = Math.sign(child.position.z) || 1;
      const isUpper = child.position.y > 1.2;
      const cellSign = new Vector3(signX, isUpper ? 1 : -1, signZ);
      child.traverse((m) => {
        if (!m.isMesh || !m.geometry) return;
        let litMat = litMatCache.get(m);
        if (!litMat) {
          litMat = createLitSatinMaterial(m.geometry, m.material, cellSign, sharedLitUniforms);
          litMatCache.set(m, litMat);
        }
        cellMaterials.push({
          mesh: m,
          origMat: m.material,
          litMat,
        });
      });
      cellTargets.push({
        obj: child,
        basePos: child.position.clone(),
        isUpper,
        signX,
        signY: isUpper ? 1 : -1,
        signZ,
      });
    }
  }

  let activeFaceStyle = '';
  function applyFaceStyle(nextStyle) {
    const resolved = nextStyle === 'lit-satin' ? 'lit-satin' : 'original';
    if (resolved === activeFaceStyle) return;
    activeFaceStyle = resolved;
    for (let i = 0; i < cellMaterials.length; i += 1) {
      const entry = cellMaterials[i];
      entry.mesh.material = resolved === 'lit-satin' ? entry.litMat : entry.origMat;
    }
  }

  // ==================== CORE + 3D VFX RIG ====================

  const coreRig = buildCoreVariant(coreStyle, { envTex, flareTex, camera });
  coreRig.root.position.copy(center);
  model.add(coreRig.root);

  const stageFloorRig = createStageFloorRig({
    bgStyle,
    envTex,
    stageTex,
    shadowTex,
    glowTex,
    rippleTex,
    horizonTex,
    holoTex,
    lowerCells: cellTargets.filter((c) => !c.isUpper),
  });
  scene.add(stageFloorRig.group);

  // Volumetric Center Glow Sprite — depthTest: true ensures all 8 opaque cubes properly occlude core light
  const auraMat = new SpriteMaterial({
    map: glowTex,
    color: '#6ce0ff',
    transparent: true,
    opacity: 0,
    blending: AdditiveBlending,
    depthWrite: false,
    depthTest: true,
  });
  const auraSprite = new Sprite(auraMat);
  auraSprite.position.copy(center);
  auraSprite.renderOrder = 0;
  model.add(auraSprite);

  // ==================== 8-CANDIDATE 3D STARDUST UPDRAFT SURROUND SYSTEM (EXPANSIONS OF CANDIDATE 2) ====================
  // Deterministic PRNG so stardust layouts are art-directed, balanced, and reproducible
  let surroundSeed = 20261115;
  const nextRand = () => {
    surroundSeed = (surroundSeed * 1664525 + 1013904223) >>> 0;
    return surroundSeed / 4294967296;
  };

  // Helper: sample a 3D position in the outer halo around the crystal silhouette
  const sampleHaloPos = (rMin = 2.75, rMax = 4.95) => {
    const ang = nextRand() * Math.PI * 2;
    const rad = rMin + Math.pow(nextRand(), 0.75) * (rMax - rMin);
    const x = Math.cos(ang) * rad * 1.04;
    const y = Math.sin(ang) * rad * 1.08;
    const z = (nextRand() - 0.5) * 3.6;
    return { x, y, z, ang, rad };
  };

  const starQuadGeo = new PlaneGeometry(1, 1);

  function createStarGlintMaterial(shape = 0, coreHex = '#ffffff', glowHex = '#48c8ff') {
    return new ShaderMaterial({
      transparent: true,
      depthWrite: false,
      depthTest: true,
      side: DoubleSide,
      blending: AdditiveBlending,
      toneMapped: false,
      uniforms: {
        uShape: { value: shape },
        uOpacity: { value: 0 },
        uColorCore: { value: new Color(coreHex) },
        uColorGlow: { value: new Color(glowHex) },
      },
      vertexShader: /* glsl */ `
        varying vec2 vUv;
        void main() {
          vUv = uv;
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
      `,
      fragmentShader: /* glsl */ `
        varying vec2 vUv;
        uniform float uShape;
        uniform float uOpacity;
        uniform vec3 uColorCore;
        uniform vec3 uColorGlow;
        void main() {
          vec2 p = (vUv - 0.5) * 2.0;
          float r = length(p);
          float alpha = 0.0;
          float coreMix = 0.0;

          if (uShape < 0.5) {
            // Shape 0: Crisp 4-point & 8-point Diffraction Cross-Star Glint
            float spikeH = exp(-abs(p.y) * 26.0) * pow(max(0.0, 1.0 - abs(p.x)), 1.55);
            float spikeV = exp(-abs(p.x) * 26.0) * pow(max(0.0, 1.0 - abs(p.y)), 1.55);
            vec2 d = vec2(p.x + p.y, p.x - p.y) * 0.70710678;
            float spikeD = (
              exp(-abs(d.y) * 32.0) * pow(max(0.0, 1.0 - abs(d.x * 1.38)), 1.9) +
              exp(-abs(d.x) * 32.0) * pow(max(0.0, 1.0 - abs(d.y * 1.38)), 1.9)
            ) * 0.52;
            float core = exp(-r * 8.5) * 1.25 + exp(-r * 3.4) * 0.26;
            alpha = (core + (spikeH + spikeV) * 1.18 + spikeD) * smoothstep(1.0, 0.72, r);
            coreMix = clamp(exp(-r * 5.2) + (spikeH + spikeV) * 0.48, 0.0, 1.0);
          } else if (uShape < 1.5) {
            // Shape 1: Classic Rising Zero-Gravity Starlight Spark (vertical light needle + bright head)
            float headY = p.y - 0.32;
            float dHead = length(vec2(p.x * 2.2, headY * 2.2));
            float headGlint = exp(-dHead * 5.8) * 1.2 + exp(-abs(headY) * 24.0) * exp(-abs(p.x) * 5.0) * 0.65;
            float tail = exp(-abs(p.x) * 18.0) * smoothstep(-0.95, 0.38, p.y) * smoothstep(0.55, 0.15, p.y);
            alpha = headGlint + tail * 0.85;
            coreMix = clamp(exp(-dHead * 4.2) * 0.9 + tail * 0.35, 0.0, 1.0);
          } else if (uShape < 2.5) {
            // Shape 2: Long Silky Photon Plume with 4-Point Crown Star at Top Tip
            float headY = p.y - 0.52;
            vec2 hp = vec2(p.x * 1.5, headY * 2.8);
            float hr = length(hp);
            float crownCore = exp(-hr * 5.2) * 1.35;
            float crownCross = (
              exp(-abs(headY) * 32.0) * pow(max(0.0, 1.0 - abs(p.x * 1.2)), 1.8) +
              exp(-abs(p.x) * 26.0) * exp(-abs(headY) * 7.5)
            ) * 0.95;
            float plumeCore = exp(-abs(p.x) * 24.0) * pow(smoothstep(-0.98, 0.54, p.y), 1.45) * smoothstep(0.68, 0.38, p.y);
            float plumeHalo = exp(-abs(p.x) * 8.5) * pow(smoothstep(-0.88, 0.50, p.y), 1.8) * smoothstep(0.62, 0.32, p.y) * 0.38;
            alpha = crownCore + crownCross + plumeCore * 0.92 + plumeHalo;
            coreMix = clamp(crownCore * 0.9 + crownCross * 0.6 + plumeCore * 0.45, 0.0, 1.0);
          } else if (uShape < 3.5) {
            // Shape 3: Soft Vertical Aurora Pillar Curtain Wisp
            float pillar = exp(-abs(p.x) * 3.2) * pow(max(0.0, 1.0 - abs(p.y)), 1.65);
            alpha = pillar * 0.32;
            coreMix = exp(-abs(p.x) * 6.5) * 0.35;
          } else {
            // Shape 4: Fine Micro-Stardust Pinpoint Glint
            float pin = exp(-r * 10.5) * 1.3 + exp(-r * 4.2) * 0.35;
            float microCross = (exp(-abs(p.x) * 30.0) * exp(-abs(p.y) * 8.0) + exp(-abs(p.y) * 30.0) * exp(-abs(p.x) * 8.0)) * 0.5;
            alpha = (pin + microCross) * smoothstep(1.0, 0.6, r);
            coreMix = clamp(exp(-r * 6.0), 0.0, 1.0);
          }

          alpha *= uOpacity;
          if (alpha < 0.004) discard;
          vec3 col = mix(uColorGlow, uColorCore, coreMix);
          gl_FragColor = vec4(col, clamp(alpha, 0.0, 1.0));
        }
      `,
    });
  }

  const fieldReticleGroup = new Group();
  fieldReticleGroup.position.copy(center);
  fieldReticleGroup.visible = false;

  // Helper to build a general updraft mote array
  const createUpdraftSet = (group, count, configFn) => Array.from({ length: count }, (_, i) => {
    const cfg = configFn(i);
    const mat = createStarGlintMaterial(cfg.shape ?? 1);
    const mesh = new Mesh(starQuadGeo, mat);
    mesh.renderOrder = cfg.renderOrder ?? 3;
    group.add(mesh);
    return { mesh, mat, ...cfg };
  });

  // 2A. Candidate 'updraft-classic': Original Candidate 2 (52 Zero-Gravity Ascending Starlight Motes & Needles)
  const candUpdraftClassic = new Group();
  const classicMotes = createUpdraftSet(candUpdraftClassic, 52, (i) => {
    const pos = sampleHaloPos(2.75, 4.95);
    const isNeedle = i % 2 === 0;
    return {
      shape: isNeedle ? 1 : 0,
      isNeedle,
      baseX: pos.x,
      baseZ: pos.z,
      yOffset: nextRand(),
      riseSpeed: 0.11 + nextRand() * 0.16,
      swayAmp: 0.12 + nextRand() * 0.18,
      swayFreq: 0.9 + nextRand() * 1.4,
      phase: nextRand() * Math.PI * 2,
      scaleX: isNeedle ? (0.26 + nextRand() * 0.14) : (0.34 + nextRand() * 0.24),
      scaleY: isNeedle ? (0.68 + nextRand() * 0.42) : (0.34 + nextRand() * 0.24),
      isSecondary: i % 3 === 1,
    };
  });

  // 2B. Candidate 'updraft-velvet': 3-Layer Depth-Stratified Stardust Rain (88 particles: Foreground Plumes + Mid Needles + Deep Micro-Dust)
  const candUpdraftVelvet = new Group();
  const velvetMotes = createUpdraftSet(candUpdraftVelvet, 88, (i) => {
    // Tier 0 (0..15): Foreground Crown Plumes; Tier 1 (16..47): Midground Needles & Stars; Tier 2 (48..87): Deep Micro-Stardust
    const tier = i < 16 ? 0 : (i < 48 ? 1 : 2);
    const pos = sampleHaloPos(tier === 2 ? 2.55 : 2.85, tier === 0 ? 4.85 : 5.10);
    const shape = tier === 0 ? 2 : (tier === 1 ? (i % 2 === 0 ? 1 : 0) : 4);
    return {
      shape,
      tier,
      baseX: pos.x,
      baseZ: tier === 0 ? (0.6 + nextRand() * 1.2) : (tier === 1 ? (nextRand() - 0.5) * 2.2 : (-0.8 - nextRand() * 1.4)),
      yOffset: nextRand(),
      riseSpeed: tier === 0 ? (0.09 + nextRand() * 0.08) : (tier === 1 ? (0.12 + nextRand() * 0.14) : (0.07 + nextRand() * 0.10)),
      swayAmp: tier === 2 ? 0.08 : (0.12 + nextRand() * 0.16),
      swayFreq: 0.8 + nextRand() * 1.3,
      phase: nextRand() * Math.PI * 2,
      scaleX: tier === 0 ? (0.38 + nextRand() * 0.14) : (tier === 1 ? (0.26 + nextRand() * 0.16) : (0.18 + nextRand() * 0.10)),
      scaleY: tier === 0 ? (1.15 + nextRand() * 0.45) : (tier === 1 ? (shape === 1 ? 0.72 + nextRand() * 0.35 : 0.36 + nextRand() * 0.18) : (0.18 + nextRand() * 0.10)),
      opBoost: tier === 0 ? 1.08 : (tier === 1 ? 0.92 : 0.68),
      isSecondary: i % 2 === 1,
    };
  });

  // 2C. Candidate 'updraft-silhouette': Contour-Hugging Anti-Gravity Levitation Stream (cradles the lower V-waist & flanks)
  const candUpdraftSilhouette = new Group();
  const silhouetteMotes = createUpdraftSet(candUpdraftSilhouette, 68, (i) => {
    const side = i % 2 === 0 ? 1 : -1;
    // Concentrated along the left/right diamond flanks (|x| = 2.35 .. 4.05), with a few under the bottom apex
    const flankT = nextRand();
    const absX = 0.65 + Math.pow(flankT, 0.55) * 3.45;
    const baseX = side * absX;
    // Lower isometric V-waist contour: bottom apex is around y = -3.6, flanks rise to y = -1.2
    const waistBottomY = -3.75 + (absX / 3.8) * 1.65;
    const isPlume = i % 3 !== 0;
    return {
      shape: isPlume ? (i % 3 === 1 ? 2 : 1) : 0,
      side,
      baseX,
      absX,
      waistBottomY,
      baseZ: (nextRand() - 0.45) * 2.4,
      yOffset: nextRand(),
      riseSpan: 5.2 + nextRand() * 2.0,
      riseSpeed: 0.12 + nextRand() * 0.15,
      phase: nextRand() * Math.PI * 2,
      scaleX: isPlume ? (0.28 + nextRand() * 0.14) : (0.36 + nextRand() * 0.22),
      scaleY: isPlume ? (0.85 + nextRand() * 0.55) : (0.36 + nextRand() * 0.22),
      isSecondary: i % 3 === 1,
    };
  });

  // 2D. Candidate 'updraft-plumes': Silky Long Crown-Star Photon Plumes + Micro-Glints
  const candUpdraftPlumes = new Group();
  const plumeMotes = createUpdraftSet(candUpdraftPlumes, 64, (i) => {
    const pos = sampleHaloPos(2.80, 4.95);
    const isLongPlume = i < 42;
    return {
      shape: isLongPlume ? 2 : 0,
      isLongPlume,
      baseX: pos.x,
      baseZ: pos.z,
      yOffset: nextRand(),
      riseSpeed: isLongPlume ? (0.13 + nextRand() * 0.15) : (0.09 + nextRand() * 0.12),
      swayAmp: isLongPlume ? (0.06 + nextRand() * 0.08) : (0.14 + nextRand() * 0.14),
      swayFreq: 0.8 + nextRand() * 1.1,
      phase: nextRand() * Math.PI * 2,
      scaleX: isLongPlume ? (0.34 + nextRand() * 0.14) : (0.30 + nextRand() * 0.20),
      scaleY: isLongPlume ? (1.35 + nextRand() * 0.85) : (0.30 + nextRand() * 0.20),
      isSecondary: i % 2 === 1,
    };
  });

  // 2E. Candidate 'updraft-vortex': Gentle 3D Helical Swirl Ascending Stardust (no rings, spiraling particle updraft)
  const candUpdraftVortex = new Group();
  const vortexMotes = createUpdraftSet(candUpdraftVortex, 66, (i) => {
    const baseAng = (i / 66) * Math.PI * 2 + (nextRand() - 0.5) * 0.25;
    const rad = 2.95 + Math.pow(nextRand(), 0.7) * 1.85;
    const isNeedle = i % 3 !== 0;
    return {
      shape: isNeedle ? (i % 3 === 1 ? 2 : 1) : 0,
      isNeedle,
      baseAng,
      rad,
      yOffset: nextRand(),
      riseSpeed: 0.11 + nextRand() * 0.13,
      swirlTurns: 1.35 + nextRand() * 0.45,
      phase: nextRand() * Math.PI * 2,
      scaleX: isNeedle ? (0.28 + nextRand() * 0.14) : (0.36 + nextRand() * 0.22),
      scaleY: isNeedle ? (0.88 + nextRand() * 0.52) : (0.36 + nextRand() * 0.22),
      isSecondary: i % 2 === 1,
    };
  });

  // 2F. Candidate 'updraft-crystals': Ascending Micro-Crystal Prisms + Stardust Updraft
  const candUpdraftCrystals = new Group();
  const crystalUpdraftMotes = createUpdraftSet(candUpdraftCrystals, 48, (i) => {
    const pos = sampleHaloPos(2.75, 4.95);
    const isNeedle = i % 2 === 0;
    return {
      shape: isNeedle ? 1 : 0,
      baseX: pos.x,
      baseZ: pos.z,
      yOffset: nextRand(),
      riseSpeed: 0.11 + nextRand() * 0.15,
      swayAmp: 0.12 + nextRand() * 0.16,
      swayFreq: 0.9 + nextRand() * 1.3,
      phase: nextRand() * Math.PI * 2,
      scaleX: isNeedle ? (0.26 + nextRand() * 0.14) : (0.34 + nextRand() * 0.22),
      scaleY: isNeedle ? (0.72 + nextRand() * 0.42) : (0.34 + nextRand() * 0.22),
      isSecondary: i % 3 === 1,
    };
  });
  const miniPrismGeo = new OctahedronGeometry(0.105, 0);
  const miniPrismEdgeGeo = new EdgesGeometry(miniPrismGeo, 1);
  const risingPrisms = Array.from({ length: 14 }, (_, i) => {
    const pos = sampleHaloPos(2.95, 4.75);
    const holder = new Group();
    const solidMat = new MeshBasicMaterial({
      color: '#38b6ff',
      transparent: true,
      opacity: 0,
      blending: AdditiveBlending,
      depthWrite: false,
      depthTest: true,
      toneMapped: false,
    });
    const wireMat = new LineBasicMaterial({
      color: '#e8fcff',
      transparent: true,
      opacity: 0,
      blending: AdditiveBlending,
      depthWrite: false,
      depthTest: true,
    });
    const body = new Mesh(miniPrismGeo, solidMat);
    const edges = new LineSegments(miniPrismEdgeGeo, wireMat);
    body.renderOrder = 3;
    edges.renderOrder = 4;
    const sx = 0.72 + nextRand() * 0.42;
    const sy = 1.25 + nextRand() * 0.75;
    holder.scale.set(sx, sy, sx);
    holder.add(body, edges);
    candUpdraftCrystals.add(holder);
    return {
      holder,
      solidMat,
      wireMat,
      baseX: pos.x,
      baseZ: pos.z,
      yOffset: i / 14,
      riseSpeed: 0.08 + nextRand() * 0.09,
      swayAmp: 0.14 + nextRand() * 0.15,
      rotSpeedX: (nextRand() - 0.5) * 1.5,
      rotSpeedY: (nextRand() - 0.5) * 2.0,
      phase: nextRand() * Math.PI * 2,
    };
  });

  // 2G. Candidate 'updraft-aurora': 3-Tone Celestial Stardust Updraft (Ice Cyan / Champagne Gold / Aurora Violet) + Vertical Aurora Curtains
  const candUpdraftAurora = new Group();
  const auroraPillars = createUpdraftSet(candUpdraftAurora, 6, (i) => {
    const side = i % 2 === 0 ? 1 : -1;
    const baseX = side * (2.65 + Math.floor(i / 2) * 0.78);
    return {
      shape: 3,
      renderOrder: 1,
      baseX,
      baseY: -0.2 + (i % 3 - 1) * 0.55,
      baseZ: -1.1 - (i % 2) * 0.6,
      scaleX: 1.45 + (i % 3) * 0.35,
      scaleY: 5.2 + (i % 2) * 1.2,
      phase: i * 1.05,
      tintIdx: i % 3,
    };
  });
  const auroraUpdraftMotes = createUpdraftSet(candUpdraftAurora, 64, (i) => {
    const pos = sampleHaloPos(2.75, 4.95);
    const isNeedle = i % 2 === 0;
    return {
      shape: isNeedle ? (i % 4 === 0 ? 2 : 1) : 0,
      baseX: pos.x,
      baseZ: pos.z,
      yOffset: nextRand(),
      riseSpeed: 0.11 + nextRand() * 0.15,
      swayAmp: 0.12 + nextRand() * 0.18,
      swayFreq: 0.9 + nextRand() * 1.3,
      phase: nextRand() * Math.PI * 2,
      scaleX: isNeedle ? (0.28 + nextRand() * 0.14) : (0.36 + nextRand() * 0.24),
      scaleY: isNeedle ? (0.78 + nextRand() * 0.52) : (0.36 + nextRand() * 0.24),
      tintIdx: i % 3, // 0: Ice Cyan, 1: Champagne Warm-Gold, 2: Aurora Violet-Blue
    };
  });

  // 2H. Candidate 'updraft-pulse': Rhythmic Tidal-Wave Stardust Updraft (synced with crystal breathing waves)
  const candUpdraftPulse = new Group();
  const pulseUpdraftMotes = createUpdraftSet(candUpdraftPulse, 68, (i) => {
    const pos = sampleHaloPos(2.75, 4.95);
    const isNeedle = i % 3 !== 2;
    return {
      shape: isNeedle ? (i % 3 === 0 ? 2 : 1) : 0,
      isNeedle,
      baseX: pos.x,
      baseZ: pos.z,
      yOffset: nextRand(),
      riseSpeed: 0.12 + nextRand() * 0.14,
      swayAmp: 0.10 + nextRand() * 0.15,
      swayFreq: 0.9 + nextRand() * 1.2,
      phase: nextRand() * Math.PI * 2,
      scaleX: isNeedle ? (0.28 + nextRand() * 0.14) : (0.34 + nextRand() * 0.22),
      scaleY: isNeedle ? (0.76 + nextRand() * 0.48) : (0.34 + nextRand() * 0.22),
      isSecondary: i % 2 === 1,
    };
  });

  const surroundCandidates = {
    'updraft-classic': candUpdraftClassic,
    'updraft-velvet': candUpdraftVelvet,
    'updraft-silhouette': candUpdraftSilhouette,
    'updraft-plumes': candUpdraftPlumes,
    'updraft-vortex': candUpdraftVortex,
    'updraft-crystals': candUpdraftCrystals,
    'updraft-aurora': candUpdraftAurora,
    'updraft-pulse': candUpdraftPulse,
  };
  for (const grp of Object.values(surroundCandidates)) {
    grp.visible = false;
    fieldReticleGroup.add(grp);
  }
  model.add(fieldReticleGroup);

  // 8 Crystal Synapse Filaments + Tether Filaments to OS Emblems
  const totalLines = cellTargets.length + osPack.textures.length;
  const linePositions = new Float32Array(totalLines * 6);
  const lineGeo = new BufferGeometry();
  const lineAttr = new Float32BufferAttribute(linePositions, 3);
  lineGeo.setAttribute('position', lineAttr);
  const lineMat = new LineBasicMaterial({
    color: '#6ce0ff',
    transparent: true,
    opacity: 0,
    blending: AdditiveBlending,
    depthWrite: false,
  });
  const filaments = new LineSegments(lineGeo, lineMat);
  model.add(filaments);

  // Animated Energy Packets traveling bidirectionally along the 8 crystal conduits
  const packetGeo = new OctahedronGeometry(0.055, 0);
  const packetMat = new MeshBasicMaterial({
    color: '#ffffff',
    transparent: true,
    opacity: 0,
    blending: AdditiveBlending,
    depthWrite: false,
    toneMapped: false,
  });
  const packets = [];
  for (let i = 0; i < cellTargets.length * 2; i += 1) {
    const p = new Mesh(packetGeo, packetMat);
    p.renderOrder = 5;
    model.add(p);
    packets.push({
      mesh: p,
      cellIdx: i % cellTargets.length,
      phaseOffset: (i * 0.19) % 1,
      reverse: i >= cellTargets.length,
    });
  }

  // 36 True 3D Spatial Crystal Shards inside WebGL Scene (real front/back parallax occlusion)
  const shardGeo = new OctahedronGeometry(0.042, 0);
  shardGeo.scale(0.85, 1.55, 0.85);
  const shardMat = new MeshBasicMaterial({
    color: '#a8ebff',
    transparent: true,
    opacity: 0,
    blending: AdditiveBlending,
    depthWrite: false,
    toneMapped: false,
  });
  const shards3d = Array.from({ length: 36 }, (_, i) => {
    const m = new Mesh(shardGeo, shardMat);
    m.renderOrder = 4;
    model.add(m);
    const angle = (i / 36) * Math.PI * 2 + (i % 3) * 0.45;
    return {
      mesh: m,
      baseAngle: angle,
      radiusMin: 1.05 + (i % 5) * 0.24,
      radiusSpan: 0.9 + ((i * 7) % 11) * 0.14,
      ySpread: (((i * 13) % 21) / 20 - 0.5) * 3.1,
      speed: (0.28 + (i % 6) * 0.09) * (i % 2 === 0 ? 1 : -1),
      bobPhase: i * 0.9,
      scale: 0.65 + (i % 4) * 0.28,
    };
  });

  // 24 Crisp 3D Micro-Crystal Particles (replacing fuzzy bokeh sprites for sharp geometric polish)
  const AMBIENT_PARTICLE_COUNT = 24;
  const ambientGeo = new OctahedronGeometry(0.036, 0);
  const ambientSprites = [];
  for (let i = 0; i < AMBIENT_PARTICLE_COUNT; i += 1) {
    const frac = (i + 0.5) / AMBIENT_PARTICLE_COUNT;
    const phi = Math.acos(1 - 2 * frac);
    const theta = i * 2.39996323;
    const radius = 3.25 + ((i * 17) % 15) * 0.11;
    const mat = new MeshBasicMaterial({
      color: i % 3 === 0 ? '#dff8ff' : '#58d4ff',
      transparent: true,
      opacity: 0.72,
      blending: AdditiveBlending,
      depthWrite: false,
      depthTest: true,
      toneMapped: false,
    });
    const sp = new Mesh(ambientGeo, mat);
    sp.renderOrder = 3;
    const baseScale = 0.75 + (i % 4) * 0.28;
    const sinPhi = Math.sin(phi);
    sp.position.set(
      center.x + radius * sinPhi * Math.cos(theta),
      center.y + radius * Math.cos(phi),
      center.z + radius * sinPhi * Math.sin(theta),
    );
    sp.scale.setScalar(baseScale);
    model.add(sp);
    ambientSprites.push({
      sprite: sp,
      mat,
      isBright: i % 3 === 0,
      radius,
      phi,
      theta,
      baseScale,
      orbitSpeed: (0.14 + (i % 7) * 0.03) * (i % 2 === 0 ? 1 : -1),
      bobSpeed: 0.75 + (i % 5) * 0.16,
      bobAmp: 0.14 + (i % 4) * 0.045,
      phase: i * 1.17,
    });
  }

  // 6 Floating OS Icons (Windows, Ubuntu, ArchLinux, Debian, openSUSE, FnOS)
  // Arranged in a balanced 3D hexagonal halo cleanly outside the 8-cube silhouette
  const cardAnchors = [
    new Vector3(4.55, -1.55, 0),       // Windows (bottom-center)
    new Vector3(1.55, -0.65, 5.25),    // Ubuntu (bottom-left)
    new Vector3(-3.45, 1.45, 4.65),    // ArchLinux (top-left)
    new Vector3(-4.55, 2.25, 0),       // Debian (top-center)
    new Vector3(-1.55, 1.35, -5.25),   // openSUSE (top-right)
    new Vector3(3.45, -0.75, -4.65),   // FnOS (bottom-right)
  ];
  const emblemPlaneGeo = new PlaneGeometry(2.15, 2.15);
  const emblems = cardAnchors.map((anchor, idx) => {
    const mat = new MeshBasicMaterial({
      map: osPack.textures[idx],
      transparent: true,
      opacity: 0,
      side: DoubleSide,
      depthWrite: false,
      alphaTest: 0.01,
      toneMapped: false,
    });
    const mesh = new Mesh(emblemPlaneGeo, mat);
    mesh.position.copy(anchor);
    mesh.renderOrder = 6;
    model.add(mesh);
    return { mesh, mat, baseAnchor: anchor };
  });

  // ==================== INTERACTION & SPRING STATE ====================

  let isDragging = false;
  let isHovered = false;
  let cardLocked = false;
  let lastInteractAt = -10000;
  let lastWheelOrbitAt = -10000;
  let pulseBoost = 0;

  let explode = 0;
  let explodeVel = 0;
  let explodeDelayed = 0;
  let fieldRingAlpha = 0;
  let pointerDownInfo = null;
  const events = new AbortController();
  const { signal } = events;

  controls.addEventListener('start', () => {
    isDragging = true;
    host.setAttribute('data-dragging', '');
  });
  controls.addEventListener('end', () => {
    isDragging = false;
    lastInteractAt = performance.now();
    host.removeAttribute('data-dragging');
  });
  controls.addEventListener('wheel-orbit', () => {
    const now = performance.now();
    lastInteractAt = now;
    lastWheelOrbitAt = now;
  });

  host.addEventListener('pointerdown', (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    pointerDownInfo = { id: e.pointerId, x: e.clientX, y: e.clientY, maxDist: 0, t: performance.now() };
  }, { signal });
  window.addEventListener('pointermove', (e) => {
    if (!pointerDownInfo || e.pointerId !== pointerDownInfo.id) return;
    const d = Math.hypot(e.clientX - pointerDownInfo.x, e.clientY - pointerDownInfo.y);
    if (d > pointerDownInfo.maxDist) pointerDownInfo.maxDist = d;
  }, { signal });
  window.addEventListener('pointerup', (e) => {
    if (!pointerDownInfo || e.pointerId !== pointerDownInfo.id) return;
    const { maxDist, t } = pointerDownInfo;
    const dtMs = performance.now() - t;
    pointerDownInfo = null;
    // Quick click/tap (< 5px excursion, < 280ms) toggles exploded inspection lock;
    // pressing/holding >= 280ms or dragging >= 5px opens while pressed and auto-closes on release.
    if (maxDist < 5 && dtMs < 280) {
      cardLocked = !cardLocked;
      pulseBoost = Math.max(pulseBoost, 0.72);
      if (cardLocked) stageFloorRig.triggerPulse();
      hoverTarget.dispatchEvent(new CustomEvent('c2-lock-changed', { bubbles: true }));
    }
  }, { signal });
  window.addEventListener('pointercancel', (e) => {
    if (pointerDownInfo && e.pointerId === pointerDownInfo.id) pointerDownInfo = null;
  }, { signal });

  function resetView() {
    controls.reset();
    pivot.rotation.set(baseRotX, 0, baseRotZ);
    pivot.position.set(0, 0, 0);
    lastInteractAt = -10000;
    syncLitUniforms(explode);
    renderer.render(scene, camera);
  }

  host.addEventListener('dblclick', (e) => {
    e.preventDefault();
    cardLocked = false;
    resetView();
    hoverTarget.dispatchEvent(new CustomEvent('c2-lock-changed', { bubbles: true }));
  }, { signal });

  host.addEventListener('keydown', (event) => {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      cardLocked = !cardLocked;
      pulseBoost = Math.max(pulseBoost, 0.75);
      if (cardLocked) stageFloorRig.triggerPulse();
      hoverTarget.dispatchEvent(new CustomEvent('c2-lock-changed', { bubbles: true }));
      return;
    }
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home'].includes(event.key)) return;
    event.preventDefault();
    if (event.key === 'Home') {
      cardLocked = false;
      resetView();
      hoverTarget.dispatchEvent(new CustomEvent('c2-lock-changed', { bubbles: true }));
      return;
    }
    const offset = camera.position.clone().sub(controls.target);
    const spherical = new Spherical().setFromVector3(offset);
    if (event.key === 'ArrowLeft') spherical.theta += 0.12;
    if (event.key === 'ArrowRight') spherical.theta -= 0.12;
    if (event.key === 'ArrowUp') spherical.phi -= 0.12;
    if (event.key === 'ArrowDown') spherical.phi += 0.12;
    spherical.phi = Math.max(controls.minPolarAngle, Math.min(controls.maxPolarAngle, spherical.phi));
    camera.position.copy(controls.target).add(offset.setFromSpherical(spherical));
    controls.update();
    lastInteractAt = performance.now();
  }, { signal });

  hoverTarget.addEventListener('pointerenter', () => { isHovered = true; }, { signal });
  hoverTarget.addEventListener('pointerleave', () => { isHovered = false; }, { signal });

  function syncSize() {
    const width = host.clientWidth || 300;
    renderer.setSize(width, width, false);
  }
  const resizeObserver = new ResizeObserver(() => {
    syncSize();
    renderer.render(scene, camera);
  });
  resizeObserver.observe(host);
  applyFaceStyle(stateGetter?.()?.faceStyle);
  syncLitUniforms(0);
  syncSize();
  renderer.render(scene, camera);
  host.setAttribute('data-ready', '');

  return {
    resetView() {
      cardLocked = false;
      resetView();
    },
    setLocked(val) {
      cardLocked = Boolean(val);
      if (cardLocked) pulseBoost = Math.max(pulseBoost, 0.65);
    },
    setFaceStyle(val) {
      applyFaceStyle(val);
      syncLitUniforms(explode);
      // Keep studio key/fill/rim lighting anchored to the viewer's screen orientation
      // so whichever faces rotate toward the front/top stay evenly illuminated
      camera.updateMatrixWorld();
      camera.matrixWorld.extractBasis(camRight, camUp, camForward);
      keyLight.position.copy(camUp).multiplyScalar(11).addScaledVector(camRight, -3.5).addScaledVector(camForward, 6);
      fillLight.position.copy(camRight).multiplyScalar(9).addScaledVector(camUp, 2).addScaledVector(camForward, 5);
      rimLight.position.copy(camForward).multiplyScalar(-7).addScaledVector(camUp, 6).addScaledVector(camRight, -4);

      renderer.render(scene, camera);
    },
    isLocked: () => cardLocked,
    syncSize,
    triggerPulse() {
      pulseBoost = 1;
      stageFloorRig.triggerPulse();
    },
    isHovered: () => isHovered || isDragging,
    update(dt, elapsed) {
      if (!host.offsetParent) return;
      const state = stateGetter();
      applyFaceStyle(state?.faceStyle);
      const isDark = state.theme === 'dark';
      const locked = cardLocked || state.globalLocked;

      // Advance smooth rotational inertia from pointer drag release or two-finger touchpad scroll
      const isCoasting = Boolean(controls.updateInertia?.(dt));

      // Multi-mode idle kinematics (all modes ramp smoothly from 0 at elapsed=0 for zero load snap)
      const nowPerf = performance.now();
      if (isCoasting) lastInteractAt = nowPerf;
      const sinceInteract = (nowPerf - lastInteractAt) / 1000;
      const isWheelOrbiting = (nowPerf - lastWheelOrbitAt) / 1000 < 0.24;
      const isActiveOrbit = isDragging || isWheelOrbiting || isCoasting;
      const canIdleMove = !isActiveOrbit && sinceInteract > 1.2;
      pulseBoost = Math.max(0, pulseBoost - dt * 1.4);
      const scalePop = 1 + pulseBoost * 0.04;

      const motionMode = state.motion || 'combo';
      const tLoad = Math.min(1, elapsed / 1.15);
      const loadRamp = tLoad * tLoad * (3 - 2 * tLoad);
      const tResume = isActiveOrbit ? 0 : Math.max(0, Math.min(1, (sinceInteract - 1.0) / 0.9));
      const activeIdle = loadRamp * (tResume * tResume * (3 - 2 * tResume));

      if (motionMode === 'static') {
        pivot.position.y += (0 - pivot.position.y) * Math.min(1, dt * 6);
        if (canIdleMove) {
          pivot.rotation.y = Math.atan2(Math.sin(pivot.rotation.y), Math.cos(pivot.rotation.y));
          pivot.rotation.y += (0 - pivot.rotation.y) * Math.min(1, dt * 5);
          pivot.rotation.x += (baseRotX - pivot.rotation.x) * Math.min(1, dt * 5);
          pivot.rotation.z += (baseRotZ - pivot.rotation.z) * Math.min(1, dt * 5);
        }
      } else if (motionMode === 'orbit') {
        // Scheme D: Continuous slow orbital rotation (~26s per turn), yielding immediately on interaction
        const floatY = Math.sin(elapsed * 1.5) * 0.13 * activeIdle;
        pivot.position.y += (floatY - pivot.position.y) * Math.min(1, dt * 7);
        if (canIdleMove) {
          pivot.rotation.y += dt * 0.24 * activeIdle;
          const swayX = baseRotX + Math.sin(elapsed * 0.65) * 0.032 * activeIdle;
          pivot.rotation.x += (swayX - pivot.rotation.x) * Math.min(1, dt * 5);
          pivot.rotation.z += (baseRotZ - pivot.rotation.z) * Math.min(1, dt * 5);
        }
      } else {
        // Schemes A ('sway'), B ('pulse-seam'), C ('field'), A+B ('combo'), or legacy 'breathe'
        if (canIdleMove) {
          pivot.rotation.y = Math.atan2(Math.sin(pivot.rotation.y), Math.cos(pivot.rotation.y));
        }
        const hasSway = motionMode === 'combo' || motionMode === 'sway';
        const floatAmp = hasSway ? 0.155 : (motionMode === 'pulse-seam' || motionMode === 'field' ? 0.095 : 0.045);
        const floatY = Math.sin(elapsed * 1.55) * floatAmp * activeIdle;
        const swayY = canIdleMove
          ? (hasSway ? Math.sin(elapsed * 0.88) * 0.105 * activeIdle : 0)
          : pivot.rotation.y;
        const swayX = canIdleMove
          ? baseRotX + (hasSway ? Math.sin(elapsed * 0.62 + 0.8) * 0.044 * activeIdle : 0)
          : pivot.rotation.x;
        pivot.position.y += (floatY - pivot.position.y) * Math.min(1, dt * 7);
        pivot.rotation.y += (swayY - pivot.rotation.y) * Math.min(1, dt * 5);
        pivot.rotation.x += (swayX - pivot.rotation.x) * Math.min(1, dt * 5);
        pivot.rotation.z += (baseRotZ - pivot.rotation.z) * Math.min(1, dt * 5);
      }
      pivot.scale.setScalar(scalePop);

      // Scheme B ('pulse-seam') & A+B ('combo'): subtle in-place crystal crevice breathing when closed
      const hasSeamPulse = motionMode === 'pulse-seam' || motionMode === 'combo';
      const seamWave = 0.5 - 0.5 * Math.cos(elapsed * 1.65);
      const idleSeamExplode = hasSeamPulse
        ? (motionMode === 'pulse-seam' ? 0.095 : 0.068) * seamWave * loadRamp
        : 0;

      // Spring-damper vs Linear explode kinematics (hover peeks hairline slit at 0.15; click-lock or pointer press-drag opens core)
      const targetExplode = (locked || isDragging) ? 1 : (isHovered ? 0.15 : idleSeamExplode);
      if (state.fxSpring) {
        const stiffness = 135;
        const damping = 14.5;
        const accel = (targetExplode - explode) * stiffness - explodeVel * damping;
        explodeVel += accel * dt;
        explode += explodeVel * dt;
        explodeDelayed += (explode - explodeDelayed) * Math.min(1, dt * 16);
      } else {
        explodeVel = 0;
        explode += (targetExplode - explode) * Math.min(1, dt * 6.5);
        explodeDelayed = explode;
      }

      if (camera.isOrthographicCamera) {
        const curExtent = srcRestExtent + (openExtent - srcRestExtent) * Math.max(0, Math.min(1.15, explode));
        if (Math.abs(camera.right - curExtent) > 0.002) {
          camera.left = -curExtent;
          camera.right = curExtent;
          camera.top = curExtent;
          camera.bottom = -curExtent;
          camera.updateProjectionMatrix();
        }
      }

      // Displace 8 outer crystals with subtle upper-lead mechanical stagger when spring active
      const spreadXZ = 1.08;
      const spreadYUp = 0.84;
      const spreadYDown = 0.84;
      for (let i = 0; i < cellTargets.length; i += 1) {
        const c = cellTargets[i];
        const e = c.isUpper ? explode : explodeDelayed;
        const spreadY = c.isUpper ? spreadYUp : spreadYDown;
        c.obj.position.set(
          c.basePos.x + c.signX * e * spreadXZ,
          c.basePos.y + c.signY * e * spreadY,
          c.basePos.z + c.signZ * e * spreadXZ,
        );
        linePositions[i * 6 + 0] = center.x;
        linePositions[i * 6 + 1] = center.y;
        linePositions[i * 6 + 2] = center.z;
        linePositions[i * 6 + 3] = c.obj.position.x;
        linePositions[i * 6 + 4] = c.obj.position.y + 0.95;
        linePositions[i * 6 + 5] = c.obj.position.z;
      }

      const clampExp = Math.max(0, explode);
      const vis = Math.max(0, Math.min(1, (clampExp - 0.18) / 0.72));
      const peekGlow = Math.max(0, Math.min(1, clampExp / 0.25));
      const seamGlowBoost = hasSeamPulse ? seamWave * loadRamp * (motionMode === 'pulse-seam' ? 0.55 : 0.38) : 0;
      const effectivePeek = Math.max(peekGlow, seamGlowBoost);

      // Center Core rig + Volumetric Glow Sprite
      coreRig.update(elapsed, Math.max(vis, effectivePeek * 0.26), pulseBoost, isDark);

      auraMat.blending = isDark ? AdditiveBlending : NormalBlending;
      auraMat.color.set(isDark ? '#6ce0ff' : '#0072e5');
      auraMat.opacity = (effectivePeek * 0.24 + vis * 0.56 + pulseBoost * 0.28) * (isDark ? 1 : 0.45);
      auraSprite.visible = state.fxGlow && auraMat.opacity > 0.01;
      const glowScale = 1.45 + vis * 2.15 + pulseBoost * 0.6;
      auraSprite.scale.set(glowScale, glowScale, 1);

      // True 3D Starlight & Non-Ring Spatial Surround (8 candidate styles + 'none')
      // Stay continuously visible during trackpad scrolling & pointer dragging (gated by loadRamp, NOT activeIdle)
      const surroundMode = state.surroundMode || 'updraft-velvet';
      const wantFieldRing = (motionMode !== 'static' && surroundMode !== 'none') ? loadRamp : 0;
      fieldRingAlpha += (wantFieldRing - fieldRingAlpha) * Math.min(1, dt * 6);
      if (fieldRingAlpha > 0.005 && surroundMode !== 'none') {
        fieldReticleGroup.visible = true;
        fieldReticleGroup.quaternion.copy(pivot.quaternion).invert().multiply(camera.quaternion);

        curCamSpherical.setFromVector3(camera.position.clone().sub(orbitTarget));
        const orbitDeltaTheta = curCamSpherical.theta - initCamSpherical.theta;
        const orbitDeltaPhi = curCamSpherical.phi - initCamSpherical.phi;
        const parallaxSinX = Math.sin(orbitDeltaTheta);
        const parallaxSinY = Math.sin(orbitDeltaPhi);

        const rScale = 0.98 + 0.02 * Math.sin(elapsed * 1.4) + clampExp * 0.34;
        fieldReticleGroup.scale.setScalar(rScale);

        for (const [key, grp] of Object.entries(surroundCandidates)) {
          grp.visible = (key === surroundMode);
        }

        const baseOp = fieldRingAlpha * (isDark ? 0.95 : 0.72) * (1 + pulseBoost * 0.35);
        const blendMode = isDark ? AdditiveBlending : NormalBlending;
        const colCoreA = isDark ? '#ffffff' : '#0055b3';
        const colGlowA = isDark ? '#46c8ff' : '#0284c7';
        const colCoreB = isDark ? '#d8f6ff' : '#0369a1';
        const colGlowB = isDark ? '#70abff' : '#2563eb';

        const applyStarTheme = (mat, op, isSecondary = false) => {
          mat.blending = blendMode;
          mat.uniforms.uColorCore.value.set(isSecondary ? colCoreB : colCoreA);
          mat.uniforms.uColorGlow.value.set(isSecondary ? colGlowB : colGlowA);
          mat.uniforms.uOpacity.value = op;
        };

        if (surroundMode === 'updraft-classic') {
          for (const m of classicMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.55 + 0.45 * Math.sin(elapsed * 2.8 + m.phase);
            const y = -4.2 + u * 8.4 + parallaxSinY * m.baseZ * 0.24;
            const x = m.baseX + Math.sin(u * Math.PI * 2 * m.swayFreq + m.phase) * m.swayAmp + parallaxSinX * m.baseZ * 0.38;
            m.mesh.position.set(x, y, m.baseZ);
            const sMult = 0.75 + 0.35 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * sMult, 1);
            applyStarTheme(m.mat, baseOp * env * tw, m.isSecondary);
          }
        } else if (surroundMode === 'updraft-velvet') {
          for (const m of velvetMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.52 + 0.48 * Math.sin(elapsed * (m.tier === 0 ? 2.2 : 3.1) + m.phase);
            const y = -4.35 + u * 8.7 + parallaxSinY * m.baseZ * 0.24;
            const x = m.baseX + Math.sin(u * Math.PI * 2 * m.swayFreq + m.phase) * m.swayAmp + parallaxSinX * m.baseZ * 0.38;
            m.mesh.position.set(x, y, m.baseZ);
            const sMult = 0.78 + 0.32 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * sMult, 1);
            applyStarTheme(m.mat, baseOp * env * tw * m.opBoost, m.isSecondary);
          }
        } else if (surroundMode === 'updraft-silhouette') {
          for (const m of silhouetteMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.55 + 0.45 * Math.sin(elapsed * 2.7 + m.phase);
            const y = m.waistBottomY + u * m.riseSpan + parallaxSinY * m.baseZ * 0.22;
            // Gently curve inward near the top diamond apex (y > 1.8) while hugging the flanks
            const topPull = Math.max(0, (y - 1.5) / 3.2);
            const x = m.baseX * (1 - topPull * 0.22) + Math.sin(u * Math.PI * 2 + m.phase) * 0.10 + parallaxSinX * m.baseZ * 0.32;
            m.mesh.position.set(x, y, m.baseZ);
            m.mesh.rotation.z = m.side * topPull * 0.14;
            const sMult = 0.76 + 0.34 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * sMult, 1);
            applyStarTheme(m.mat, baseOp * env * tw, m.isSecondary);
          }
        } else if (surroundMode === 'updraft-plumes') {
          for (const m of plumeMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.58 + 0.42 * Math.sin(elapsed * 2.5 + m.phase);
            const y = -4.3 + u * 8.6 + parallaxSinY * m.baseZ * 0.24;
            const x = m.baseX + Math.sin(u * Math.PI * 2 * m.swayFreq + m.phase) * m.swayAmp + parallaxSinX * m.baseZ * 0.38;
            m.mesh.position.set(x, y, m.baseZ);
            const sMult = 0.80 + 0.30 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * (0.85 + 0.25 * env), 1);
            applyStarTheme(m.mat, baseOp * env * tw, m.isSecondary);
          }
        } else if (surroundMode === 'updraft-vortex') {
          for (const m of vortexMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.55 + 0.45 * Math.sin(elapsed * 2.8 + m.phase);
            const y = -4.1 + u * 8.2;
            const ang = m.baseAng + u * m.swirlTurns + elapsed * 0.15 - orbitDeltaTheta;
            const rCur = m.rad * (0.92 + 0.12 * Math.sin(u * Math.PI));
            const x = Math.cos(ang) * rCur;
            const z = Math.sin(ang) * (rCur * 0.65);
            m.mesh.position.set(x, y + parallaxSinY * z * 0.24, z);
            // Tilt needle slightly along the 3D helical tangent
            m.mesh.rotation.z = m.isNeedle ? (-Math.sin(ang) * 0.22) : 0;
            const sMult = 0.76 + 0.34 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * sMult, 1);
            applyStarTheme(m.mat, baseOp * env * tw, m.isSecondary);
          }
        } else if (surroundMode === 'updraft-crystals') {
          for (const m of crystalUpdraftMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.55 + 0.45 * Math.sin(elapsed * 2.8 + m.phase);
            const y = -4.2 + u * 8.4 + parallaxSinY * m.baseZ * 0.24;
            const x = m.baseX + Math.sin(u * Math.PI * 2 * m.swayFreq + m.phase) * m.swayAmp + parallaxSinX * m.baseZ * 0.38;
            m.mesh.position.set(x, y, m.baseZ);
            const sMult = 0.75 + 0.35 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * sMult, 1);
            applyStarTheme(m.mat, baseOp * env * tw, m.isSecondary);
          }
          for (const pr of risingPrisms) {
            const u = (elapsed * pr.riseSpeed + pr.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const y = -3.9 + u * 7.8 + parallaxSinY * pr.baseZ * 0.24;
            const x = pr.baseX + Math.sin(u * Math.PI * 2 + pr.phase) * pr.swayAmp + parallaxSinX * pr.baseZ * 0.38;
            pr.holder.position.set(x, y, pr.baseZ);
            pr.holder.rotation.set(
              pr.phase + elapsed * pr.rotSpeedX + orbitDeltaPhi * 0.6,
              pr.phase * 0.7 + elapsed * pr.rotSpeedY - orbitDeltaTheta * 0.8,
              Math.sin(elapsed + pr.phase) * 0.25,
            );
            pr.solidMat.blending = blendMode;
            pr.solidMat.color.set(isDark ? '#38b6ff' : '#0284c7');
            pr.solidMat.opacity = fieldRingAlpha * (isDark ? 0.52 : 0.42) * env;
            pr.wireMat.blending = blendMode;
            pr.wireMat.color.set(isDark ? '#e8fcff' : '#0052a8');
            pr.wireMat.opacity = fieldRingAlpha * (isDark ? 0.94 : 0.80) * env;
          }
        } else if (surroundMode === 'updraft-aurora') {
          for (const pl of auroraPillars) {
            const breathe = 0.65 + 0.35 * Math.sin(elapsed * 0.85 + pl.phase);
            pl.mesh.position.set(
              pl.baseX + Math.sin(elapsed * 0.4 + pl.phase) * 0.18 + parallaxSinX * pl.baseZ * 0.25,
              pl.baseY + parallaxSinY * pl.baseZ * 0.18,
              pl.baseZ,
            );
            pl.mesh.scale.set(pl.scaleX, pl.scaleY * (0.92 + 0.16 * breathe), 1);
            pl.mat.blending = blendMode;
            pl.mat.uniforms.uColorCore.value.set(isDark ? '#e0f8ff' : '#0055b3');
            pl.mat.uniforms.uColorGlow.value.set(
              isDark
                ? (pl.tintIdx === 0 ? '#38bdf8' : (pl.tintIdx === 1 ? '#67e8f9' : '#818cf8'))
                : '#0284c7',
            );
            pl.mat.uniforms.uOpacity.value = baseOp * 0.58 * breathe;
          }
          for (const m of auroraUpdraftMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const tw = 0.55 + 0.45 * Math.sin(elapsed * 2.8 + m.phase);
            const y = -4.2 + u * 8.4 + parallaxSinY * m.baseZ * 0.24;
            const x = m.baseX + Math.sin(u * Math.PI * 2 * m.swayFreq + m.phase) * m.swayAmp + parallaxSinX * m.baseZ * 0.38;
            m.mesh.position.set(x, y, m.baseZ);
            const sMult = 0.76 + 0.34 * tw;
            m.mesh.scale.set(m.scaleX * sMult, m.scaleY * sMult, 1);
            m.mat.blending = blendMode;
            if (isDark) {
              if (m.tintIdx === 0) {
                m.mat.uniforms.uColorCore.value.set('#ffffff');
                m.mat.uniforms.uColorGlow.value.set('#38bdf8');
              } else if (m.tintIdx === 1) {
                m.mat.uniforms.uColorCore.value.set('#fef9c3');
                m.mat.uniforms.uColorGlow.value.set('#67e8f9');
              } else {
                m.mat.uniforms.uColorCore.value.set('#eef2ff');
                m.mat.uniforms.uColorGlow.value.set('#818cf8');
              }
            } else {
              m.mat.uniforms.uColorCore.value.set('#0055b3');
              m.mat.uniforms.uColorGlow.value.set(m.tintIdx === 2 ? '#4f46e5' : '#0284c7');
            }
            m.mat.uniforms.uOpacity.value = baseOp * env * tw;
          }
        } else if (surroundMode === 'updraft-pulse') {
          for (const m of pulseUpdraftMotes) {
            const u = (elapsed * m.riseSpeed + m.yOffset) % 1;
            const env = Math.sin(u * Math.PI);
            const y = -4.2 + u * 8.4 + parallaxSinY * m.baseZ * 0.24;
            const x = m.baseX + Math.sin(u * Math.PI * 2 * m.swayFreq + m.phase) * m.swayAmp + parallaxSinX * m.baseZ * 0.38;
            // Upward-sweeping anti-gravity wave crest (syncs with crystal breathing)
            const waveCrest = Math.pow(0.5 + 0.5 * Math.sin((u * 1.8 - elapsed * 0.48) * Math.PI * 2), 2.2);
            const tw = 0.42 + 0.58 * waveCrest;
            m.mesh.position.set(x, y, m.baseZ);
            const stretchY = m.isNeedle ? (0.72 + 0.68 * waveCrest) : (0.75 + 0.45 * waveCrest);
            m.mesh.scale.set(m.scaleX * (0.8 + 0.3 * waveCrest), m.scaleY * stretchY, 1);
            applyStarTheme(m.mat, baseOp * env * tw * 1.08, m.isSecondary);
          }
        }
      } else {
        fieldReticleGroup.visible = false;
      }

      const showEmblems = vis > 0.03;
      for (let i = 0; i < emblems.length; i += 1) {
        const em = emblems[i];
        em.mesh.visible = showEmblems;
        const px = em.baseAnchor.x * (0.35 + clampExp * 0.65);
        const py = center.y + em.baseAnchor.y + Math.sin(elapsed * 1.8 + i * 1.4) * 0.12 * clampExp;
        const pz = em.baseAnchor.z * (0.35 + clampExp * 0.65);
        if (showEmblems) {
          em.mesh.position.set(px, py, pz);
          em.mesh.scale.setScalar(0.22 + vis * 0.78);
          em.mesh.quaternion.copy(pivot.quaternion).invert().multiply(camera.quaternion);
          em.mat.opacity = vis * 0.96;
        }

        const tIdx = cellTargets.length + i;
        const tetherInner = coreStyle === 'void-nexus' ? 0.24 : (coreStyle === 'relic-gem' ? 0.72 : 0.90);
        const dir = new Vector3(px, py - center.y, pz).normalize();
        linePositions[tIdx * 6 + 0] = center.x + dir.x * tetherInner;
        linePositions[tIdx * 6 + 1] = center.y + dir.y * tetherInner;
        linePositions[tIdx * 6 + 2] = center.z + dir.z * tetherInner;
        linePositions[tIdx * 6 + 3] = px - dir.x * 0.95;
        linePositions[tIdx * 6 + 4] = py - dir.y * 0.95;
        linePositions[tIdx * 6 + 5] = pz - dir.z * 0.95;
      }
      lineAttr.needsUpdate = true;

      // Flowing Energy Conduits & Packets
      const showConduits = state.fxConduits && vis > 0.02;
      filaments.visible = showConduits;
      lineMat.blending = isDark ? AdditiveBlending : NormalBlending;
      lineMat.color.set(isDark ? '#6ce0ff' : '#0068d8');
      lineMat.opacity = vis * (isDark ? 0.56 : 0.48);

      packetMat.blending = isDark ? AdditiveBlending : NormalBlending;
      packetMat.color.set(isDark ? '#ffffff' : '#0088ff');
      packetMat.opacity = vis * (isDark ? 0.95 : 0.85);
      for (const pk of packets) {
        pk.mesh.visible = showConduits;
        if (!showConduits) continue;
        const base = pk.cellIdx * 6;
        const speed = 0.62 + pulseBoost * 1.1;
        const u = (elapsed * speed + pk.phaseOffset) % 1;
        const t = pk.reverse ? 1 - u : u;
        pk.mesh.position.set(
          linePositions[base + 0] + (linePositions[base + 3] - linePositions[base + 0]) * t,
          linePositions[base + 1] + (linePositions[base + 4] - linePositions[base + 1]) * t,
          linePositions[base + 2] + (linePositions[base + 5] - linePositions[base + 2]) * t,
        );
        const s = 0.7 + Math.sin(t * Math.PI) * 0.65;
        pk.mesh.scale.setScalar(s);
      }

      // 3D Spatial Interior Shards
      const showShards = state.fxShards && vis > 0.02;
      shardMat.blending = isDark ? AdditiveBlending : NormalBlending;
      shardMat.color.set(isDark ? '#a8ebff' : '#007be8');
      shardMat.opacity = vis * (isDark ? 0.78 : 0.66);
      for (const sh of shards3d) {
        sh.mesh.visible = showShards;
        if (!showShards) continue;
        const ang = sh.baseAngle + elapsed * sh.speed;
        const rad = sh.radiusMin + vis * sh.radiusSpan;
        sh.mesh.position.set(
          center.x + Math.cos(ang) * rad,
          center.y + sh.ySpread * vis + Math.sin(elapsed * 1.4 + sh.bobPhase) * 0.16,
          center.z + Math.sin(ang) * rad,
        );
        sh.mesh.rotation.y = ang * 2;
        sh.mesh.scale.setScalar(sh.scale * (0.4 + vis * 0.6));
      }

      // Ambient 3D Orbital Micro-Crystals (hidden when closed so resting icon matches static WebP cleanly)
      const showAmbient = Boolean(state.fxShards) && vis > 0.02;
      const animTime = state.motion === 'static' ? 0 : elapsed;
      const expandOut = clampExp * 1.15;
      for (let i = 0; i < ambientSprites.length; i += 1) {
        const a = ambientSprites[i];
        a.sprite.visible = showAmbient;
        if (!showAmbient) continue;
        const r = a.radius + expandOut;
        const th = a.theta + animTime * a.orbitSpeed;
        const sinPhi = Math.sin(a.phi);
        a.sprite.position.set(
          center.x + r * sinPhi * Math.cos(th),
          center.y + r * Math.cos(a.phi) + Math.sin(animTime * a.bobSpeed + a.phase) * a.bobAmp,
          center.z + r * sinPhi * Math.sin(th),
        );
        const twinkle = 0.55 + 0.45 * Math.sin(animTime * 1.85 + a.phase);
        a.sprite.rotation.set(a.phase + animTime * 0.55, th * 1.4, a.phase * 0.5);
        a.sprite.scale.setScalar(a.baseScale * (0.82 + 0.30 * twinkle));
        a.mat.blending = isDark ? AdditiveBlending : NormalBlending;
        a.mat.color.set(isDark ? (a.isBright ? '#dff8ff' : '#64d8ff') : (a.isBright ? '#0070e0' : '#1890ff'));
        a.mat.opacity = vis * (isDark ? 0.68 : 0.52) * (0.55 + 0.45 * twinkle) * (0.85 + pulseBoost * 0.25);
      }

      // Update world transforms before sampling lower Crystal columns for grounded 3D shadows
      syncLitUniforms(clampExp);
      pivot.updateWorldMatrix(true, true);
      stageFloorRig.update(dt, elapsed, camera.position, pivot.position.y, clampExp, vis, pulseBoost, isDark, state.fxGlow);

      // Keep studio key/fill/rim lighting anchored to the viewer's screen orientation
      // so whichever faces rotate toward the front/top stay evenly illuminated
      camera.updateMatrixWorld();
      camera.matrixWorld.extractBasis(camRight, camUp, camForward);
      keyLight.position.copy(camUp).multiplyScalar(11).addScaledVector(camRight, -3.5).addScaledVector(camForward, 6);
      fillLight.position.copy(camRight).multiplyScalar(9).addScaledVector(camUp, 2).addScaledVector(camForward, 5);
      rimLight.position.copy(camForward).multiplyScalar(-7).addScaledVector(camUp, 6).addScaledVector(camRight, -4);

      renderer.render(scene, camera);
    },
    destroy() {
      events.abort();
      resizeObserver.disconnect();
      controls.dispose();
      envTex.dispose();
      disposeObjectTree(scene);
      for (const mat of litMatCache.values()) mat.dispose();
      for (let i = 0; i < cellMaterials.length; i += 1) {
        const entry = cellMaterials[i];
        if (Array.isArray(entry.origMat)) {
          for (const m of entry.origMat) m?.dispose?.();
        } else {
          entry.origMat?.dispose?.();
        }
      }
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
      host.removeAttribute('data-ready');
      host.removeAttribute('data-dragging');
    },
  };
}
