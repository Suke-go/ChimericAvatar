// Copyright (c) 2025 naruya
// Licensed under the MIT License. See LICENSE file in the project root for full license information.


import * as THREE from 'three';
import * as GVRMUtils from './utils.js';
import { VRMCharacter } from './vrm.js';
import { GaussianSplatting } from './gs.js';
import { PLYParser } from './ply.js';
import JSZip from 'jszip'


export class GVRM extends THREE.Group {
  constructor(character, gs) {
    super();
    this.character = character;
    this.gs = gs;
    this.debugAxes = new Map();
    this.isReady = false;
    this.t = 0;
  }

  static async initVRM(vrmPath, scene, camera, renderer, modelScale, boneOperations, expectedVertexCount) {
    if ( !boneOperations ) {
      boneOperations = (await (await fetch("./assets/default.json")).json()).boneOperations;
    }
    if ( !modelScale ) {
      modelScale = 1.0;
    }
    const character = new VRMCharacter(scene, vrmPath, '', modelScale, true);
    await character.loadingPromise;

    character.skinnedMeshIndex = 1;
    character.faceIndex = undefined;
    if (character.currentVrm.scene.children.length > 4) {
      character.skinnedMeshIndex = 2;
      character.faceIndex = 1;
    }

    // Local fork: pick the SkinnedMesh that matches the server preprocess.
    // The server (`avatar_preprocess.py`) binds splats against ONE
    // primitive of the body mesh (the largest skinned primitive). Three.js
    // GLTFLoader creates a separate SkinnedMesh per glTF primitive even
    // when they share a skeleton, so the runtime must pick the same one
    // — otherwise `splatVertexIndices` reference vertex IDs that don't
    // exist on the runtime mesh and the shader's anchor lookup samples
    // zero-initialized texture data, producing NaN splat positions.
    //
    // Selection: prefer the mesh whose vertex count exactly matches
    // `_serverMeshVertexCount` (passed in as expectedVertexCount). Fall
    // back to the LARGEST SkinnedMesh if no exact match.
    let skinnedMesh = null;
    let bestCount = -1;
    let exactMatch = null;
    const allMeshInfo = [];
    character.currentVrm.scene.traverse((obj) => {
      if (obj.isSkinnedMesh && obj.skeleton && obj.geometry
          && obj.geometry.attributes && obj.geometry.attributes.position) {
        const count = obj.geometry.attributes.position.count;
        allMeshInfo.push({ name: obj.name, count });
        if (expectedVertexCount && count === expectedVertexCount && !exactMatch) {
          exactMatch = obj;
        }
        if (count > bestCount) {
          bestCount = count;
          skinnedMesh = obj;
        }
      }
    });
    if (exactMatch) {
      skinnedMesh = exactMatch;
      console.log(`[gvrm] picked SkinnedMesh by exact vertex match (${expectedVertexCount}):`, exactMatch.name);
    } else if (skinnedMesh) {
      console.log(
        `[gvrm] picked LARGEST SkinnedMesh (no exact match for expected ${expectedVertexCount ?? 'n/a'}):`,
        skinnedMesh.name, 'count:', bestCount, 'all meshes:', allMeshInfo,
      );
    }
    if (skinnedMesh) {
      const idx = character.currentVrm.scene.children.indexOf(skinnedMesh);
      character.skinnedMeshIndex = idx >= 0 ? idx : 1;
      character.faceIndex = undefined;
    }
    // Cache resolved mesh — used by getPointsMeshCapsules / gsCustomize
    // Material when scene.children[skinnedMeshIndex] doesn't expose it
    // directly (three-vrm 3.x with combineSkeletons hides the SkinnedMesh
    // inside a Group).
    character.discoveredSkinnedMesh = skinnedMesh;

    // Step-by-step phase logs so console clearly shows WHICH init step
    // throws when the cryptic "elements" error fires. Each step in
    // isolation: visualizeVRM, setPose, skeleton update, normal compute,
    // HeadTop_End synth+rebind, renderer.render, bindMatrix snapshot,
    // boneTexture0 snapshot.
    console.log('[gvrm] initVRM: visualizeVRM…');
    GVRMUtils.visualizeVRM(character, false);

    console.log('[gvrm] initVRM: setPose…');
    GVRMUtils.setPose(character, boneOperations);

    console.log('[gvrm] initVRM: skeleton.update + computeBoneTexture…');
    if (skinnedMesh && skinnedMesh.skeleton) {
      // character.currentVrm.scene.updateMatrixWorld(true);
      skinnedMesh.skeleton.update();
      skinnedMesh.skeleton.computeBoneTexture();
    }
    console.log('[gvrm] initVRM: computeVertexNormals…');
    if (skinnedMesh && skinnedMesh.geometry) {
      skinnedMesh.geometry.computeVertexNormals();
    }

    // Synthesize J_Bip_C_HeadTop_End if it doesn't already exist in the
    // skeleton. The server preprocess always emits one (it's required for
    // capsule binding of head splats). The upstream code only synthesized
    // it for skinnedMeshIndex===2; we do it unconditionally so server +
    // runtime bone counts always agree.
    console.log('[gvrm] initVRM: HeadTop_End synth check…');
    if (skinnedMesh && skinnedMesh.skeleton) {
      const hasHeadTopEnd = skinnedMesh.skeleton.bones.some(
        b => b.name === "J_Bip_C_HeadTop_End"
      );
      if (!hasHeadTopEnd) {
        console.log('[gvrm] initVRM: synthesizing HeadTop_End…');
        const headNode = character.currentVrm.humanoid.getRawBoneNode('head');
        if (headNode) {
          const headTopEndNode = new THREE.Bone();
          headTopEndNode.name = "J_Bip_C_HeadTop_End";
          headTopEndNode.position.set(0, 0.2, -0.05);
          headTopEndNode.updateMatrixWorld(true);
          headNode.add(headTopEndNode);

          // CRITICAL: do NOT mutate `skinnedMesh.skeleton.bones` in place.
          // Without three-vrm's removeUnnecessaryJoints/combineSkeletons,
          // every primitive of the body mesh ends up as a separate
          // SkinnedMesh sharing one Skeleton instance. Pushing to
          // `bones` mutates the array on the SHARED object, but the
          // shared `boneInverses` does NOT grow — so on the next
          // WebGLRenderer.render every other SkinnedMesh that still
          // points at the old skeleton crashes inside Skeleton.update at
          // `bones[i].matrixWorld * boneInverses[i]` (boneInverses[i] is
          // undefined). Build fresh bones + boneInverses arrays here and
          // bind the body to a brand-new Skeleton so the original is
          // untouched and other meshes continue rendering.
          const oldBones = skinnedMesh.skeleton.bones;
          const oldInverses = skinnedMesh.skeleton.boneInverses;
          const newBones = [...oldBones, headTopEndNode];
          const newInverses = [
            ...oldInverses,
            new THREE.Matrix4().copy(headTopEndNode.matrixWorld).invert(),
          ];
          skinnedMesh.bind(
            new THREE.Skeleton(newBones, newInverses),
            skinnedMesh.matrixWorld,
          );
        }
      } else {
        console.log('[gvrm] initVRM: HeadTop_End already present');
      }
    }
    // call renderer.render after skinnedMesh.bind (?)
    console.log('[gvrm] initVRM: renderer.render…');
    renderer.render(scene, camera);  // ???
    console.log('[gvrm] initVRM: renderer.render OK');

    // do not use .clone(), texture.image will be shared unexpectedly
    // const boneTexture0 = skinnedMesh.skeleton.boneTexture.clone();
    console.log('[gvrm] initVRM: bindMatrix snapshot…');
    if (skinnedMesh && skinnedMesh.bindMatrix) {
      skinnedMesh.bindMatrix0 = skinnedMesh.bindMatrix.clone();
      skinnedMesh.bindMatrixInverse0 = skinnedMesh.bindMatrixInverse.clone();
    } else {
      console.warn('[gvrm] initVRM: skinnedMesh has no bindMatrix — bindMatrix0/Inverse0 will be undefined');
    }
    console.log('[gvrm] initVRM: bindMatrix snapshot OK', {
      hasBindMatrix: !!skinnedMesh?.bindMatrix,
      hasBindMatrix0: !!skinnedMesh?.bindMatrix0,
    });

    // Snapshot the rest-pose bone texture for use as `boneTexture0` by the
    // shader-injected skinning math. three-vrm 3.x's combineSkeletons +
    // our manual bind() with a new Skeleton may leave the previous
    // boneTexture stale. We rebuild from `skeleton.boneMatrices` directly
    // — that's a Float32Array of `bones.length * 16` floats which is
    // exactly what three.js packs into a square RGBA float texture.
    if (skinnedMesh && skinnedMesh.skeleton) {
      const skel = skinnedMesh.skeleton;
      // Force texture compute if not present.
      if (!skel.boneTexture || !skel.boneTexture.image || !skel.boneTexture.image.data) {
        skel.computeBoneTexture();
      }
      // Update bone matrices to current pose (rest pose at this point).
      skel.update();
      const boneTex = skel.boneTexture;
      if (boneTex && boneTex.image && boneTex.image.data) {
        // Slice ensures the snapshot doesn't share memory with the live
        // texture (which mutates each frame).
        const dataCopy = boneTex.image.data.slice();
        skinnedMesh.boneTexture0 = new THREE.DataTexture(
          dataCopy, boneTex.image.width, boneTex.image.height, boneTex.format, boneTex.type,
        );
        skinnedMesh.boneTexture0.needsUpdate = true;
      } else if (skel.boneMatrices) {
        // Fallback: build from boneMatrices directly. Three.js sizes the
        // bone texture to the next power-of-two square big enough for
        // `bones.length * 16` floats packed as RGBA.
        const boneCount = skel.bones.length;
        const required = boneCount * 16;
        let size = 1;
        while (size * size * 4 < required) size *= 2;
        const data = new Float32Array(size * size * 4);
        data.set(skel.boneMatrices);
        skinnedMesh.boneTexture0 = new THREE.DataTexture(
          data, size, size, THREE.RGBAFormat, THREE.FloatType,
        );
        skinnedMesh.boneTexture0.needsUpdate = true;
        // Three.js needs the LIVE boneTexture too — recreate if absent.
        if (!skel.boneTexture) {
          const liveData = new Float32Array(size * size * 4);
          liveData.set(skel.boneMatrices);
          skel.boneTexture = new THREE.DataTexture(
            liveData, size, size, THREE.RGBAFormat, THREE.FloatType,
          );
          skel.boneTexture.needsUpdate = true;
        }
      } else {
        console.warn(
          '[gvrm] could not build boneTexture0 — skeleton has neither boneTexture nor boneMatrices.',
        );
      }
    }
    console.log('[gvrm] initVRM: complete', {
      hasBoneTexture0: !!skinnedMesh?.boneTexture0,
      hasLiveBoneTexture: !!skinnedMesh?.skeleton?.boneTexture,
      boneCount: skinnedMesh?.skeleton?.bones?.length,
    });

    return character;
  }


  static async initGS(gsPath, gsPosition, gsQuaternion, scene, formatHint) {
    // formatHint: "ply" | "spz" | undefined. Forwarded to GS3D's
    // SceneFormat to bypass extension sniffing on blob: URLs.
    const gs = await new GaussianSplatting(gsPath, 1, gsPosition, gsQuaternion, formatHint);

    await gs.loadingPromise;  // TODO: refactor
    scene.add(gs);

    gs.splatMesh = gs.viewer.splatMesh;
    gs.centers = gs.splatMesh.splatDataTextures.baseData.centers;
    gs.colors = gs.splatMesh.splatDataTextures.baseData.colors;
    gs.covariances = gs.splatMesh.splatDataTextures.baseData.covariances;
    gs.splatCount = gs.splatMesh.geometry.attributes.splatIndex.array.length;

    gs.centers0 = new Float32Array(gs.centers);
    gs.colors0 = new Float32Array(gs.colors);
    gs.covariances0 = new Float32Array(gs.covariances);
    gs.splatMesh.updateDataTexturesFromBaseData(0, gs.splatCount - 1);

    return gs;
  }

  static async loadStaticPreview(url, scene) {
    console.log('Loading static GVRM preview:', url);
    const response = await fetch(url);
    const zip = await JSZip.loadAsync(response.arrayBuffer());
    const plyEntry = zip.file('model.ply');
    if (!plyEntry) {
      throw new Error("No model.ply found inside the .gvrm zip");
    }

    const plyBuffer = await plyEntry.async('arraybuffer');
    const extraData = JSON.parse(await zip.file('data.json').async('text'));
    const plyBlob = new Blob([plyBuffer], { type: 'application/octet-stream' });
    const plyUrl = URL.createObjectURL(plyBlob);

    try {
      const gs = await GVRM.initGS(
        plyUrl,
        extraData.gsPosition,
        extraData.gsQuaternion,
        scene,
        "ply",
      );
      return {
        gs,
        update() {},
        dispose() {
          gs.viewer?.dispose?.();
        },
      };
    } finally {
      URL.revokeObjectURL(plyUrl);
    }
  }


  static async load(url, scene, camera, renderer, fileName) {
    console.log('Loading GVRM:', url);
    const response = await fetch(url);
    const zip = await JSZip.loadAsync(response.arrayBuffer());
    const vrmBuffer = await zip.file('model.vrm').async('arraybuffer');
    // Server-side preprocess always emits PLY (alignment + bone bindings
    // baked in). SPZ output was dropped because Niantic v4 SPZ isn't
    // decodable by mkkellogg/gaussian-splats-3d 0.4.7. The .gvrm contract
    // is now (model.vrm, model.ply, data.json).
    const plyEntry = zip.file('model.ply');
    if (!plyEntry) {
      throw new Error("No model.ply found inside the .gvrm zip");
    }
    const plyBuffer = await plyEntry.async('arraybuffer');
    const extraData = JSON.parse(await zip.file('data.json').async('text'));

    const vrmBlob = new Blob([vrmBuffer], { type: 'application/octet-stream' });
    const vrmUrl = URL.createObjectURL(vrmBlob);

    const plyBlob = new Blob([plyBuffer], { type: 'application/octet-stream' });
    const plyUrl = URL.createObjectURL(plyBlob);

    const modelScale = extraData.modelScale;
    const boneOperations = extraData.boneOperations;

    if (extraData.splatRelativePoses === undefined) {  // TODO: remove
      extraData.splatRelativePoses = extraData.relativePoses;
    }

    const character = await GVRM.initVRM(
      vrmUrl, scene, camera, renderer, modelScale, boneOperations,
      extraData._serverMeshVertexCount,
    );
    console.log('[gvrm] load: initVRM returned, starting bone remap…');

    // Remap splatBoneIndices from server preprocess's bone order to the
    // runtime skeleton's order. The server emits `_serverBoneNames`
    // (indexed by its own bone_index); we resolve each name against the
    // runtime skeleton.bones to produce the runtime-correct index.
    // MUST run before sortSplatsByBones (which keys boneSceneMap by these
    // indices) so per-bone scene transforms in updateByBones land on the
    // matching bone instead of being silently skipped.
    if (extraData._serverBoneNames && character.discoveredSkinnedMesh?.skeleton) {
      const serverNames = extraData._serverBoneNames;
      const runtimeBones = character.discoveredSkinnedMesh.skeleton.bones;
      const nameToRuntimeIdx = new Map();
      runtimeBones.forEach((b, i) => nameToRuntimeIdx.set(b.name, i));
      let unresolvedCount = 0;
      const remapped = new Array(extraData.splatBoneIndices.length);
      for (let i = 0; i < extraData.splatBoneIndices.length; i++) {
        const serverIdx = extraData.splatBoneIndices[i];
        const name = serverNames[serverIdx];
        const runtimeIdx = name !== undefined ? nameToRuntimeIdx.get(name) : undefined;
        if (runtimeIdx === undefined) {
          unresolvedCount++;
          remapped[i] = serverIdx; // fall back; better than 0
        } else {
          remapped[i] = runtimeIdx;
        }
      }
      extraData.splatBoneIndices = remapped;
      console.log(
        `[gvrm] bone-index remap: ${unresolvedCount}/${remapped.length} unresolved ` +
        `(server bones: ${serverNames.length}, runtime bones: ${runtimeBones.length})`,
      );
    }

    console.log('[gvrm] load: sortSplatsByBones…');
    const { sceneSplatIndices, boneSceneMap } = GVRM.sortSplatsByBones(extraData);
    console.log('[gvrm] load: splitPLY…', { scenes: Object.keys(sceneSplatIndices).length });
    const parser = new PLYParser();
    const sceneUrls = await parser.splitPLY(plyUrl, sceneSplatIndices);
    URL.revokeObjectURL(plyUrl);

    // formatHint = "ply": every sceneUrl here is a blob URL produced by
    // PLYParser.splitPLY, so GS3D can't sniff the extension from the URL.
    // Without this hint GS3D throws "File format not supported".
    console.log('[gvrm] load: initGS…');
    const gs = await GVRM.initGS(
      sceneUrls, extraData.gsPosition, extraData.gsQuaternion, scene, "ply",
    );
    sceneUrls.forEach(url => URL.revokeObjectURL(url));
    console.log('[gvrm] load: initGS OK, splatCount:', gs.splatCount);

    const gvrm = new GVRM(character, gs);
    gvrm.modelScale = modelScale;
    gvrm.boneOperations = boneOperations;
    // dynamic sort (choose one map)
    gvrm.boneSceneMap = boneSceneMap;
    // gvrm.vertexSceneMap = vertexSceneMap;
    gvrm.fileName = fileName;

    gvrm.updatePMC();
    GVRMUtils.addPMC(scene, gvrm.pmc);
    GVRMUtils.visualizePMC(gvrm.pmc, false);
    renderer.render(scene, camera);

    gvrm.gs.splatVertexIndices = extraData.splatVertexIndices ?? [];
    gvrm.gs.splatBoneIndices = extraData.splatBoneIndices ?? [];
    gvrm.gs.splatRelativePoses = extraData.splatRelativePoses ?? [];
    // Hard requirement: bindings array length must match splat count.
    // Anything else is a server-side preprocess bug that we want to
    // surface, not silently paper over.
    if (gvrm.gs.splatVertexIndices.length !== gvrm.gs.splatCount) {
      throw new Error(
        `splatVertexIndices length ${gvrm.gs.splatVertexIndices.length} != splatCount ${gvrm.gs.splatCount} ` +
        `(server preprocess produced inconsistent data.json — rebuild)`,
      );
    }

    // Diagnostic: validate server preprocess assumptions hold against the
    // runtime VRM. The server uses raw glTF vertex/bone order; the runtime
    // (post-removal of removeUnnecessaryVertices/Joints) should match.
    {
      const sm = character.discoveredSkinnedMesh;
      const runtimeVtxCount = sm?.geometry?.attributes?.position?.count ?? -1;
      const runtimeBoneCount = sm?.skeleton?.bones?.length ?? -1;
      const runtimeBoneNames = sm?.skeleton?.bones?.map(b => b.name) ?? [];
      const serverBoneNames = extraData._serverBoneNames ?? [];
      let maxVtxIdx = -1, maxBoneIdx = -1;
      for (let i = 0; i < gvrm.gs.splatVertexIndices.length; i++) {
        if (gvrm.gs.splatVertexIndices[i] > maxVtxIdx) maxVtxIdx = gvrm.gs.splatVertexIndices[i];
      }
      for (let i = 0; i < gvrm.gs.splatBoneIndices.length; i++) {
        if (gvrm.gs.splatBoneIndices[i] > maxBoneIdx) maxBoneIdx = gvrm.gs.splatBoneIndices[i];
      }
      let relMin = Infinity, relMax = -Infinity, relSum = 0;
      for (let i = 0; i < gvrm.gs.splatCount; i++) {
        const d = Math.hypot(
          gvrm.gs.splatRelativePoses[i * 3 + 0],
          gvrm.gs.splatRelativePoses[i * 3 + 1],
          gvrm.gs.splatRelativePoses[i * 3 + 2],
        );
        if (d < relMin) relMin = d;
        if (d > relMax) relMax = d;
        relSum += d;
      }
      console.log('[gvrm] diagnostic:', {
        splatCount: gvrm.gs.splatCount,
        runtimeVtxCount, maxVtxIdx,
        runtimeBoneCount, maxBoneIdx,
        relPose: { min: relMin, mean: relSum / Math.max(1, gvrm.gs.splatCount), max: relMax },
        buildVersion: extraData._buildVersion,
        alignmentTranslation: extraData._alignmentTranslation,
      });
      if (maxVtxIdx >= runtimeVtxCount) {
        console.error(
          `[gvrm] splatVertexIndices max ${maxVtxIdx} >= runtime mesh vertex count ${runtimeVtxCount}.`,
          ' Server preprocess vertex order does not match runtime.',
        );
      }
      if (maxBoneIdx >= runtimeBoneCount) {
        console.error(
          `[gvrm] splatBoneIndices max ${maxBoneIdx} >= runtime skeleton bone count ${runtimeBoneCount}.`,
          ' Server preprocess bone order does not match runtime.',
        );
      }
      if (serverBoneNames.length > 0 && runtimeBoneNames.length > 0) {
        const mismatch = [];
        const limit = Math.min(serverBoneNames.length, runtimeBoneNames.length);
        for (let i = 0; i < limit; i++) {
          if (serverBoneNames[i] !== runtimeBoneNames[i]) {
            mismatch.push({ idx: i, server: serverBoneNames[i], runtime: runtimeBoneNames[i] });
            if (mismatch.length >= 5) break;
          }
        }
        if (mismatch.length > 0) {
          console.warn('[gvrm] bone order mismatch (first 5):', mismatch);
        } else if (serverBoneNames.length === runtimeBoneNames.length) {
          console.log('[gvrm] bone order: server and runtime match exactly.');
        }
      }
    }

    console.log('[gvrm] load: gsCustomizeMaterial…');
    GVRM.gsCustomizeMaterial(character, gs);
    console.log('[gvrm] load: gsCustomizeMaterial OK');

    // Cleanup splats that are too far from the associated bone. The
    // upstream pass uses tight thresholds (0.1m for foot, 0.2m for body,
    // 0.3m for head) tuned for naruya's specific test data. Our server
    // preprocess (`avatar_preprocess.py`) ALREADY does the bone-binding
    // selection via capsule proxies — we don't need the runtime to
    // second-guess. Server-v2 emits clean bindings; skip the runtime
    // cleanup so we don't alpha-zero ~64% of legitimate body splats.
    //
    // Legacy "stub-v1" / unset _buildVersion still falls through to the
    // upstream cleanup as a safety net (those builds didn't run our
    // preprocess and may have garbage bindings).
    const isServerBuild = typeof extraData._buildVersion === "string"
      && extraData._buildVersion.startsWith("server-");
    const hasBindings = (
      gvrm.gs.splatBoneIndices.length === gvrm.gs.splatCount &&
      gvrm.gs.splatRelativePoses.length === gvrm.gs.splatCount * 3
    );
    if (isServerBuild) {
      console.log(
        `[gvrm] cleanup: skipped (build=${extraData._buildVersion}, ` +
        `server preprocess already filtered bindings)`,
      );
    } else if (hasBindings) {
      const resolvedSkel = character.discoveredSkinnedMesh?.skeleton;
      const findBoneIdx = (name) => {
        if (!resolvedSkel) return -1;
        const idx = resolvedSkel.bones.findIndex(b => b.name === name);
        return idx;
      };
      const headIdx = findBoneIdx('J_Bip_C_Head');
      const headTopIdx = findBoneIdx('J_Bip_C_HeadTop_End');
      const lFootIdx = findBoneIdx('J_Bip_L_Foot');
      const rFootIdx = findBoneIdx('J_Bip_R_Foot');
      let zeroed = 0;
      for (let i = 0; i < gvrm.gs.splatCount; i++) {
        const distance = Math.sqrt(
          gvrm.gs.splatRelativePoses[i * 3 + 0] ** 2 +
          gvrm.gs.splatRelativePoses[i * 3 + 1] ** 2 +
          gvrm.gs.splatRelativePoses[i * 3 + 2] ** 2,
        );
        const idx = gvrm.gs.splatBoneIndices[i];
        let kill = false;
        if (idx === lFootIdx && distance > 0.1) kill = true;
        else if (idx === rFootIdx && distance > 0.1) kill = true;
        else if (idx === headIdx && distance > 0.3) kill = true;
        else if (idx !== headIdx && idx !== headTopIdx && distance > 0.2) kill = true;
        if (kill) {
          gvrm.gs.colors[i * 4 + 3] = 0;
          zeroed++;
        }
      }
      console.log(`[gvrm] cleanup: ${zeroed}/${gvrm.gs.splatCount} splats alpha-zeroed (legacy)`,
        { headIdx, headTopIdx, lFootIdx, rFootIdx });
    }

    gvrm.gs.splatMesh.updateDataTexturesFromBaseData(0, gvrm.gs.splatCount - 1);


    // Snapshot rest-pose `matrixWorld` for EVERY bone in the skeleton.
    // The upstream code only snapshotted a hardcoded shortlist (arms/
    // legs/spine/head). With `removeUnnecessaryJoints` disabled (we
    // need the raw glTF bone order to align with the server preprocess)
    // the runtime keeps additional bones, and `updateByBones` will read
    // `matrixWorld0` on any bone that has a sceneIndex — so every bone
    // needs a rest-pose snapshot or `.clone()` throws on undefined.
    const _snappedSkel = character.discoveredSkinnedMesh?.skeleton;
    if (_snappedSkel) {
      _snappedSkel.bones.forEach((bone) => {
        bone.updateMatrixWorld(true);
        bone.matrixWorld0 = bone.matrixWorld.clone();
      });
    }

    // Store initial world position/quaternion for subscene support
    gvrm.vrmWorldPosition0 = new THREE.Vector3();
    gvrm.vrmWorldQuaternion0 = new THREE.Quaternion();
    character.currentVrm.scene.getWorldPosition(gvrm.vrmWorldPosition0);
    character.currentVrm.scene.getWorldQuaternion(gvrm.vrmWorldQuaternion0);

    gvrm.isReady = true

    return gvrm;
  }

  static async save(gvrm, vrmPath, gsPath, boneOperations, modelScale, fileName, savePly=false) {
    const vrmBuffer = await fetch(vrmPath).then(response => response.arrayBuffer());
    const plyBuffer = await fetch(gsPath).then(response => response.arrayBuffer());

    const extraData = {
      modelScale: modelScale,
      boneOperations: boneOperations,
      gsQuaternion: gvrm.gs.viewer.splatMesh.scenes[0].quaternion.toArray(),
      gsPosition: gvrm.gs.viewer.splatMesh.scenes[0].position.toArray(),
      splatVertexIndices: gvrm.gs.splatVertexIndices,
      splatBoneIndices: gvrm.gs.splatBoneIndices,
      splatRelativePoses: gvrm.gs.splatRelativePoses,
    };

    const zip = new JSZip();

    zip.file('model.vrm', vrmBuffer);
    zip.file('model.ply', plyBuffer);
    zip.file('data.json', JSON.stringify(extraData, null, 2));

    const content = await zip.generateAsync({ type: 'blob' });

    if (!fileName && gsPath.endsWith('.ply')) {
      fileName = gsPath.split('/').pop().replace('.ply', '.gvrm');
    } else if (!fileName) {  // blob
      fileName = gsPath.split('/').pop() + '.gvrm';
    }

    _downloadBlob(content, fileName);

    if (savePly) {
      console.log('savePly!');
      const plyBlob = new Blob([plyBuffer], { type: 'application/octet-stream' });
      const plyFileName = fileName.replace('.gvrm', '_processed.ply');
      _downloadBlob(plyBlob, plyFileName);
    }

    function _downloadBlob(blob, fileName) {
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName;
      a.click();
      if (blob === content && gvrm.url) {  // GVRM
        URL.revokeObjectURL(gvrm.url);
      }
      if (blob === content) {  // GVRM
        gvrm.url = url;
      }
       else {  // PLY
        URL.revokeObjectURL(url);
      }
    }
  }


  static async remove(gvrm, scene) {
    if (gvrm.character) {
      await gvrm.character.leave(scene);
      gvrm.character = null;
    }

    if (gvrm.gs) {
      await gvrm.gs.viewer.dispose();
      gvrm.gs = null;
    }

    if (gvrm.pmc) {
      GVRMUtils.removePMC(scene, gvrm.pmc);
    }
  }

  async load(url, scene, camera, renderer, fileName=null) {
    const _gvrm = await GVRM.load(url, scene, camera, renderer, fileName);

    // TODO: refactor
    this.character = _gvrm.character;
    // this.character.animationUrl = animationUrl;
    // this.character.currentMixer = currentMixer;
    this.gs = _gvrm.gs;
    this.modelScale = _gvrm.modelScale;
    this.boneOperations = _gvrm.boneOperations;
    this.boneSceneMap = _gvrm.boneSceneMap;
    this.vertexSceneMap = _gvrm.vertexSceneMap;
    this.fileName = _gvrm.fileName;
    this.vrmWorldPosition0 = _gvrm.vrmWorldPosition0;
    this.vrmWorldQuaternion0 = _gvrm.vrmWorldQuaternion0;
    this.isReady = true;
  }

  async save(vrmPath, gsPath, boneOperations, modelScale, fileName, savePly=false) {
    await GVRM.save(this, vrmPath, gsPath, boneOperations, modelScale, fileName, savePly);
  }

  async remove(scene) {
    this.isReady = false;
    await GVRM.remove(this, scene);
  }

  async changeFBX(url) {
    // GVRMUtils.resetPose(this.character, this.boneOperations);
    await this.character.changeFBX(url);
  }

  updatePMC() {
    const { pmc } = GVRMUtils.getPointsMeshCapsules(this.character);
    this.pmc = pmc;
  }

  updateByBones() {
    // Local fork: SPZ-bundled .gvrm files load without per-bone scene
    // splitting (PLYParser doesn't support SPZ), so `boneSceneMap` stays
    // null and there's nothing to update. Returning early avoids the
    // hardcoded `scene.children[2].skeleton` access below, which crashes
    // when the body mesh isn't at index 2 (Three.js groups multi-prim
    // meshes into THREE.Group, hiding the SkinnedMesh + skeleton one
    // level deeper).
    if (!this.boneSceneMap) return;

    const tempNodePos = new THREE.Vector3();
    const tempChildPos = new THREE.Vector3();
    const tempMidPoint = new THREE.Vector3();
    const tempMat = new THREE.Matrix4();
    const tempQuat = new THREE.Quaternion();
    const gsViewerMatrixWorldInverse = new THREE.Matrix4();
    const gsViewerWorldQuat = new THREE.Quaternion();
    const gsViewerWorldQuatInverse = new THREE.Quaternion();
    const noSortBoneList = [
      "J_Bip_C_Neck", "J_Bip_C_Spine", "J_Bip_C_Chest", "J_Bip_C_UpperChest", "J_Bip_C_HeadTop_End", "J_Bip_C_Head"
    ];

    // Use the resolved SkinnedMesh's skeleton instead of the hardcoded
    // children[2] index which crashes when three-vrm 3.x nests the
    // SkinnedMesh inside a Group.
    const resolvedMesh = this.character.discoveredSkinnedMesh
      || this.character.currentVrm.scene.children[this.character.skinnedMeshIndex]
      || this.character.currentVrm.scene.children[2];
    const skeleton = resolvedMesh && resolvedMesh.skeleton;
    if (!skeleton) {
      console.warn("[gvrm] updateByBones: no skeleton on resolved SkinnedMesh");
      return;
    }

    // Get GS viewer's world transform for coordinate conversion
    this.gs.viewer.updateMatrixWorld();
    gsViewerMatrixWorldInverse.copy(this.gs.viewer.matrixWorld).invert();
    this.gs.viewer.getWorldQuaternion(gsViewerWorldQuat);
    gsViewerWorldQuatInverse.copy(gsViewerWorldQuat).invert();

    skeleton.bones.forEach((bone) => {
      const children = bone.children;
      if (children.length === 0) return;

      children.forEach(childBone => {
        const childIndex = skeleton.bones.indexOf(childBone);
        const sceneIndex = this.boneSceneMap[childIndex];
        if (sceneIndex === undefined) return;

        bone.updateMatrixWorld(true);
        childBone.updateMatrixWorld(true);
        tempNodePos.setFromMatrixPosition(bone.matrixWorld);
        tempChildPos.setFromMatrixPosition(childBone.matrixWorld);
        tempMidPoint.addVectors(tempNodePos, tempChildPos).multiplyScalar(0.5);

        // Convert world coords directly to GS viewer's local coords
        tempMidPoint.applyMatrix4(gsViewerMatrixWorldInverse);

        // Rotation: bone's rotation change in GS viewer's local coords.
        // Lazy-snapshot `matrixWorld0` here for any bone that escaped
        // the init-time pass (newly synthesized bones, late-added rigs,
        // etc.) so we don't crash on `.clone()` of undefined.
        if (!childBone.matrixWorld0) {
          childBone.matrixWorld0 = childBone.matrixWorld.clone();
        }
        tempMat.extractRotation(childBone.matrixWorld.multiply(childBone.matrixWorld0.clone().invert()));
        tempQuat.setFromRotationMatrix(tempMat);
        tempQuat.premultiply(gsViewerWorldQuatInverse);
        tempQuat.multiply(this.gs.quaternion0);

        const scene = this.gs.viewer.getSplatScene(sceneIndex);
        if (scene) {
          if (!noSortBoneList.includes(childBone.name)) {
            scene.position.copy(tempMidPoint);
            scene.quaternion.copy(tempQuat);
          }
          let axesHelper = this.debugAxes.get(sceneIndex);
          if (!axesHelper) {
            axesHelper = this.createDebugAxes(sceneIndex);
          }
          axesHelper.position.copy(tempMidPoint);
          axesHelper.quaternion.copy(tempQuat);
          // axesHelper.quaternion.copy(tempQuat);
        }
      });
    });
  }

  // deprecated
  // updateByVertices() {}

  createDebugAxes(sceneIndex) {
    const axesHelper = new THREE.AxesHelper(0.3);
    axesHelper.visible = false;
    this.gs.add(axesHelper);
    this.debugAxes.set(sceneIndex, axesHelper);
    return axesHelper;
  }

  update() {
    if (!this.isReady) return;
    // Use local position/quaternion (relative to parent scene)
    let tempQuat = this.character.currentVrm.scene.quaternion.clone();
    let tempQuat0 = this.character.currentVrm.scene.quaternion0.clone();
    let tempPos = this.character.currentVrm.scene.position.clone();
    let tempPos0 = this.character.currentVrm.scene.position0.clone();
    this.gs.viewer.quaternion.copy(tempQuat.multiply(tempQuat0.invert()));
    this.gs.viewer.position.copy(tempPos.sub(tempPos0));
    // this.t += 1.0; GVRMUtils.simpleAnim(this.character, this.t);  // debug
    this.updateByBones();
    this.character.update();
  }

  static sortSplatsByBones(extraData) {
    const sceneSplatIndices = {};

    let sceneCount = 0;
    const boneSceneMap = {};

    for (let i = 0; i < extraData.splatBoneIndices.length; i++) {
      const boneIndex = extraData.splatBoneIndices[i];

      if (boneSceneMap[boneIndex] === undefined) {
        boneSceneMap[boneIndex] = sceneCount;
        sceneCount++;
        sceneSplatIndices[boneSceneMap[boneIndex]] = [];
      }
      sceneSplatIndices[boneSceneMap[boneIndex]].push(i);
    }

    GVRM.updateExtraData(extraData, sceneSplatIndices);

    return { sceneSplatIndices, boneSceneMap };
  }


  // deprecated
  // static sortSplatsByVertices(extraData) {}


  static updateExtraData(extraData, sceneSplatIndices) {

    let splatIndices = [];
    for (let i = 0; i < Object.keys(sceneSplatIndices).length; i++) {
      splatIndices = splatIndices.concat(sceneSplatIndices[i]);
    }

    const splatVertexIndices = [];
    const splatBoneIndices = [];
    const splatRelativePoses = [];

    for (const sceneIndex of Object.keys(sceneSplatIndices)) {
      for (const splatIndex of sceneSplatIndices[sceneIndex]) {
        splatVertexIndices.push(extraData.splatVertexIndices[splatIndex]);
        splatBoneIndices.push(extraData.splatBoneIndices[splatIndex]);
        splatRelativePoses.push(
          extraData.splatRelativePoses[splatIndex * 3],
          extraData.splatRelativePoses[splatIndex * 3 + 1],
          extraData.splatRelativePoses[splatIndex * 3 + 2]
        );
      }
    }

    extraData.splatVertexIndices = splatVertexIndices;
    extraData.splatBoneIndices = splatBoneIndices;
    extraData.splatRelativePoses = splatRelativePoses;
  }


  static gsCustomizeMaterial(character, gs) {

    gs.splatMesh.material = gs.splatMesh.material.clone();
    gs.splatMesh.material.needsUpdate = true;

    // Use the SkinnedMesh resolved by initVRM (handles three-vrm 3.x
    // combineSkeletons nesting). Fall back to legacy lookup for runtimes
    // that don't go through our initVRM discovery (defensive).
    let skinnedMesh = character.discoveredSkinnedMesh
      || character.currentVrm.scene.children[character.skinnedMeshIndex];
    if (!skinnedMesh || !skinnedMesh.geometry) {
      throw new Error("gsCustomizeMaterial: no SkinnedMesh with geometry found in VRM scene");
    }

    const meshVertexCount = skinnedMesh.geometry.attributes.position.count;

    const meshPositions = skinnedMesh.geometry.attributes.position.array;
    const meshNormals = skinnedMesh.geometry.attributes.normal.array;
    const meshSkinIndices = skinnedMesh.geometry.attributes.skinIndex.array;
    const meshSkinWeights = skinnedMesh.geometry.attributes.skinWeight.array;
    const gsVertexIndices = gs.splatVertexIndices;
    const gsRelativePoses = gs.splatRelativePoses;

    const bindingTextureWidth = 4096;
    const textureHeightForCount = (count) => Math.max(1, Math.ceil(count / bindingTextureWidth));
    const meshTextureHeight = textureHeightForCount(meshVertexCount);
    const gsTextureHeight = textureHeightForCount(gs.splatCount);
    const meshTextureFloatCount = bindingTextureWidth * meshTextureHeight * 4;
    const gsTextureFloatCount = bindingTextureWidth * gsTextureHeight * 4;

    const meshPositionData = new Float32Array(meshTextureFloatCount);
    const meshNormalData = new Float32Array(meshTextureFloatCount);
    const meshSkinIndexData = new Float32Array(meshTextureFloatCount);
    const meshSkinWeightData = new Float32Array(meshTextureFloatCount);
    const gsMeshVertexIndexData = new Float32Array(gsTextureFloatCount);
    const gsMeshRelativePosData = new Float32Array(gsTextureFloatCount);

    GVRMUtils.addChannels(meshPositions, meshPositionData, meshVertexCount, 1);
    GVRMUtils.addChannels(meshNormals, meshNormalData, meshVertexCount, 1);
    meshSkinIndexData.set(meshSkinIndices);
    meshSkinWeightData.set(meshSkinWeights);
    GVRMUtils.addChannels(gsVertexIndices, gsMeshVertexIndexData, gs.splatCount, 3);
    GVRMUtils.addChannels(gsRelativePoses, gsMeshRelativePosData, gs.splatCount, 1);

    const meshPositionTexture = GVRMUtils.createDataTexture(
      meshPositionData, bindingTextureWidth, meshTextureHeight, THREE.RGBAFormat, THREE.FloatType);
    const meshNormalTexture = GVRMUtils.createDataTexture(
      meshNormalData, bindingTextureWidth, meshTextureHeight, THREE.RGBAFormat, THREE.FloatType);
    const meshSkinIndexTexture = GVRMUtils.createDataTexture(
      meshSkinIndexData, bindingTextureWidth, meshTextureHeight, THREE.RGBAFormat, THREE.FloatType);
    const meshSkinWeightTexture = GVRMUtils.createDataTexture(
      meshSkinWeightData, bindingTextureWidth, meshTextureHeight, THREE.RGBAFormat, THREE.FloatType);
    const gsMeshVertexIndexTexture = GVRMUtils.createDataTexture(
      gsMeshVertexIndexData, bindingTextureWidth, gsTextureHeight, THREE.RGBAFormat, THREE.FloatType);
    const gsMeshRelativePosTexture = GVRMUtils.createDataTexture(
      gsMeshRelativePosData, bindingTextureWidth, gsTextureHeight, THREE.RGBAFormat, THREE.FloatType);

    // Pre-validate every uniform value. If any of these is undefined,
    // three.js's UniformsLib commits `value.elements` on the matrix
    // uniform and throws "Cannot read properties of undefined (reading
    // 'elements')" — a terrible message that hides which uniform is
    // missing. Surface a precise error here instead.
    const requiredUniforms = {
      'skinnedMesh.bindMatrix0': skinnedMesh.bindMatrix0,
      'skinnedMesh.bindMatrix': skinnedMesh.bindMatrix,
      'skinnedMesh.bindMatrixInverse0': skinnedMesh.bindMatrixInverse0,
      'skinnedMesh.bindMatrixInverse': skinnedMesh.bindMatrixInverse,
      'skinnedMesh.boneTexture0': skinnedMesh.boneTexture0,
      'skinnedMesh.skeleton.boneTexture': skinnedMesh.skeleton && skinnedMesh.skeleton.boneTexture,
      'character.currentVrm.scene.matrixWorld': character.currentVrm.scene.matrixWorld,
      'gs.matrix0': gs.matrix0,
      'gs.viewer.matrixWorld': gs.viewer && gs.viewer.matrixWorld,
    };
    const missing = Object.entries(requiredUniforms)
      .filter(([, v]) => v === undefined || v === null)
      .map(([k]) => k);
    if (missing.length > 0) {
      throw new Error(
        `gsCustomizeMaterial: required uniform value(s) undefined: ${missing.join(', ')}. ` +
        `This usually means initVRM() couldn't snapshot bindMatrix/boneTexture before render.`,
      );
    }

    gs.splatMesh.material.onBeforeCompile = function (shader) {
      shader.uniforms.meshPositionTexture = { value: meshPositionTexture };
      shader.uniforms.meshNormalTexture = { value: meshNormalTexture };
      shader.uniforms.meshSkinIndexTexture = { value: meshSkinIndexTexture };
      shader.uniforms.meshSkinWeightTexture = { value: meshSkinWeightTexture };
      shader.uniforms.gsMeshVertexIndexTexture = { value: gsMeshVertexIndexTexture };
      shader.uniforms.gsMeshRelativePosTexture = { value: gsMeshRelativePosTexture };
      shader.uniforms.meshBindingTextureSize = {
        value: new THREE.Vector2(bindingTextureWidth, meshTextureHeight),
      };
      shader.uniforms.gsBindingTextureSize = {
        value: new THREE.Vector2(bindingTextureWidth, gsTextureHeight),
      };
      shader.uniforms.bindMatrix0 = { value: skinnedMesh.bindMatrix0 };
      shader.uniforms.bindMatrix = { value: skinnedMesh.bindMatrix };
      shader.uniforms.bindMatrixInverse0 = { value: skinnedMesh.bindMatrixInverse0 };
      shader.uniforms.bindMatrixInverse = { value: skinnedMesh.bindMatrixInverse };
      shader.uniforms.boneTexture0 = { value: skinnedMesh.boneTexture0 };
      shader.uniforms.boneTexture = { value: skinnedMesh.skeleton.boneTexture };
      shader.uniforms.meshMatrixWorld = { value: character.currentVrm.scene.matrixWorld };
      shader.uniforms.gsMatrix0 = { value: gs.matrix0 };
      shader.uniforms.gsMatrix = { value: gs.viewer.matrixWorld };

      // console.log('Vertex Shader:', shader.vertexShader);
      // console.log('Fragment Shader:', shader.fragmentShader);

      shader.vertexShader = shader.vertexShader.replace(
        '#include <common>',
        `
        #define USE_SKINNING

        #include <common>
        #include <skinning_pars_vertex>  // boneTexture

        uniform sampler2D meshPositionTexture;
        uniform sampler2D meshNormalTexture;
        uniform sampler2D meshSkinIndexTexture;
        uniform sampler2D meshSkinWeightTexture;
        uniform sampler2D gsMeshVertexIndexTexture;
        uniform sampler2D gsMeshRelativePosTexture;
        uniform vec2 meshBindingTextureSize;
        uniform vec2 gsBindingTextureSize;
        uniform mat4 meshMatrixWorld;
        uniform mat4 gsMatrix0;
        uniform mat4 gsMatrix;

        uniform mat4 bindMatrix0;
        uniform mat4 bindMatrixInverse0;
        uniform highp sampler2D boneTexture0;

        mat4 getBoneMatrix0( const in float i ) {
          int size = textureSize( boneTexture0, 0 ).x;
          int j = int( i ) * 4;
          int x = j % size;
          int y = j / size;
          vec4 v1 = texelFetch( boneTexture0, ivec2( x, y ), 0 );
          vec4 v2 = texelFetch( boneTexture0, ivec2( x + 1, y ), 0 );
          vec4 v3 = texelFetch( boneTexture0, ivec2( x + 2, y ), 0 );
          vec4 v4 = texelFetch( boneTexture0, ivec2( x + 3, y ), 0 );
          return mat4( v1, v2, v3, v4 );
        }

        vec2 bindingIndexToUV(float index, vec2 textureSize) {
          float y = floor(index / textureSize.x);
          float x = index - y * textureSize.x;
          return (vec2(x, y) + vec2(0.5)) / textureSize;
        }

        // TODO: check this
        vec4 quatFromMat3(mat3 m) {
          float trace = m[0][0] + m[1][1] + m[2][2];
          vec4 q;

          if (trace > 0.0) {
            float s = 0.5 / sqrt(trace + 1.0);
            q.w = 0.25 / s;
            q.x = (m[2][1] - m[1][2]) * s;
            q.y = (m[0][2] - m[2][0]) * s;
            q.z = (m[1][0] - m[0][1]) * s;
          } else if (m[0][0] > m[1][1] && m[0][0] > m[2][2]) {
            float s = 2.0 * sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]);
            q.w = (m[2][1] - m[1][2]) / s;
            q.x = 0.25 * s;
            q.y = (m[0][1] + m[1][0]) / s;
            q.z = (m[0][2] + m[2][0]) / s;
          } else if (m[1][1] > m[2][2]) {
            float s = 2.0 * sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]);
            q.w = (m[0][2] - m[2][0]) / s;
            q.x = (m[0][1] + m[1][0]) / s;
            q.y = 0.25 * s;
            q.z = (m[1][2] + m[2][1]) / s;
          } else {
            float s = 2.0 * sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]);
            q.w = (m[1][0] - m[0][1]) / s;
            q.x = (m[0][2] + m[2][0]) / s;
            q.y = (m[1][2] + m[2][1]) / s;
            q.z = 0.25 * s;
          }
          return q;
        }

        vec4 quatInverse(vec4 q) {
          return vec4(-q.x, -q.y, -q.z, q.w) / dot(q, q);
        }

        vec4 quatMultiply(vec4 a, vec4 b) {
          return vec4(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
          );
        }

        mat3 mat3FromQuat(vec4 q) {
          float x = q.x, y = q.y, z = q.z, w = q.w;
          float x2 = x + x, y2 = y + y, z2 = z + z;
          float xx = x * x2, xy = x * y2, xz = x * z2;
          float yy = y * y2, yz = y * z2, zz = z * z2;
          float wx = w * x2, wy = w * y2, wz = w * z2;

          return mat3(
            1.0 - (yy + zz), xy - wz, xz + wy,
            xy + wz, 1.0 - (xx + zz), yz - wx,
            xz - wy, yz + wx, 1.0 - (xx + yy)
          );
        }
        `
      );

      shader.vertexShader = shader.vertexShader.replace(
        'mat4 transform = transforms[sceneIndex]',  // transforms are used for sorting only
        'mat4 transform = gsMatrix * gsMatrix0;'  // order
      );

      shader.vertexShader = shader.vertexShader.replace(
        'vec3 splatCenter = uintBitsToFloat(uvec3(sampledCenterColor.gba));',
        `
        vec2 samplerUV2 = bindingIndexToUV(float(splatIndex), gsBindingTextureSize);
        float meshVertexIndex = texture2D(gsMeshVertexIndexTexture, samplerUV2).r;
        vec3 relativePos = texture2D(gsMeshRelativePosTexture, samplerUV2).rgb;

        vec2 samplerUV3 = bindingIndexToUV(meshVertexIndex, meshBindingTextureSize);
        vec3 transformed = texture2D(meshPositionTexture, samplerUV3).rgb;
        vec3 objectNormal = texture2D(meshNormalTexture, samplerUV3).rgb;
        vec4 skinIndex = texture2D(meshSkinIndexTexture, samplerUV3);
        vec4 skinWeight = texture2D(meshSkinWeightTexture, samplerUV3);

        mat4 boneMatX0 = getBoneMatrix0( skinIndex.x );
        mat4 boneMatY0 = getBoneMatrix0( skinIndex.y );
        mat4 boneMatZ0 = getBoneMatrix0( skinIndex.z );
        mat4 boneMatW0 = getBoneMatrix0( skinIndex.w );
        mat4 skinMatrix0 = mat4( 0.0 );
        skinMatrix0 += skinWeight.x * boneMatX0;
        skinMatrix0 += skinWeight.y * boneMatY0;
        skinMatrix0 += skinWeight.z * boneMatZ0;
        skinMatrix0 += skinWeight.w * boneMatW0;
        skinMatrix0 = bindMatrixInverse0 * skinMatrix0 * bindMatrix0;

        #include <skinbase_vertex>  // boneMat
        #include <skinnormal_vertex>  // skinMatrix, using normal
        #include <defaultnormal_vertex>  // ?
        #include <skinning_vertex>

        // vec3 splatCenter = ( vec4(transformed, 1.0) ).xyz;
        // vec3 splatCenter = ( meshMatrixWorld * vec4(transformed, 1.0) ).xyz;
        // vec3 splatCenter = ( meshMatrixWorld * vec4(transformed + relativePos, 1.0) ).xyz;  // GOOD

        vec3 skinnedRelativePos = vec4( skinMatrix * inverse(skinMatrix0) * vec4( relativePos, 0.0 ) ).xyz;
        vec3 splatCenter = ( meshMatrixWorld * vec4(transformed + skinnedRelativePos, 1.0) ).xyz;
        `
      );

      shader.vertexShader = shader.vertexShader.replace(
        // NOTE: transformModelViewMatrix == viewMatrix * transform;  // SplatMaterial.js 143
        'vec4 viewCenter = transformModelViewMatrix * vec4(splatCenter, 1.0);',
        `
        // The splatCenter is the coordinate system for inverse(transform).
        splatCenter = (inverse(transform) * vec4(splatCenter, 1.0)).xyz;
        vec4 viewCenter = transformModelViewMatrix * vec4(splatCenter, 1.0);
        `
      );

      shader.vertexShader = shader.vertexShader.replace(
        'mat3 cov2Dm = transpose(T) * Vrk * T;',
        `
        // for debug
        // Vrk[0][0] *= 25.0; Vrk[1][1] *= 0.1; Vrk[2][2] *= 0.1;
        // Vrk[1][1] *= 25.0; Vrk[0][0] *= 0.1; Vrk[2][2] *= 0.1;
        // Vrk[2][2] *= 25.0; Vrk[0][0] *= 0.1; Vrk[1][1] *= 0.1;

        // via quat
        mat3 gsRotation0 = mat3(gsMatrix0);
        mat3 skinRotationMatrix = mat3(skinMatrix * inverse(skinMatrix0));
        mat3 relativeRotation = transpose(gsRotation0) * skinRotationMatrix * gsRotation0;
        vec4 tempQuat = quatFromMat3(relativeRotation);
        tempQuat.y = -tempQuat.y;  // Hardcode, maybe bug in quatFromMat3?
        relativeRotation = mat3FromQuat(tempQuat);
        mat3 rotatedVrk = transpose(relativeRotation) * Vrk * relativeRotation;
        mat3 cov2Dm = transpose(T) * rotatedVrk * T;

        // TODO: via mat
        // mat3 gsRotation0 = mat3(gsMatrix0);
        // mat3 skinRotationMatrix = mat3(skinMatrix * inverse(skinMatrix0));
        // mat3 relativeRotation = transpose(gsRotation0) * skinRotationMatrix * gsRotation0;
        // mat3 rotatedVrk = transpose(relativeRotation) * Vrk * relativeRotation;
        // mat3 cov2Dm = transpose(T) * rotatedVrk * T;
        `
      );
    };
    gs.splatMesh.material.needsUpdate = true;
  }
}


export * as GVRMUtils from './utils.js';
