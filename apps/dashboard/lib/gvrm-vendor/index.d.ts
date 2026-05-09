// Type shim for the vendored MIT JS modules from naruya/gaussian-vrm.
// We treat the runtime API as `unknown` and narrow on use rather than
// hand-typing 28KB of dynamic Three.js extensions — TypeScript stays out
// of the way of the upstream code while still failing the build if the
// imports vanish.

declare module "*/gvrm-vendor/gvrm-format/gvrm.js" {
  export const GVRM: {
    load: (
      url: string,
      scene: unknown,
      camera: unknown,
      renderer: unknown,
      fileName?: string,
    ) => Promise<unknown>;
    save: (...args: unknown[]) => Promise<Blob>;
    initVRM: (...args: unknown[]) => Promise<unknown>;
    initGS: (...args: unknown[]) => Promise<unknown>;
    sortSplatsByBones: (extraData: unknown) => unknown;
    gsCustomizeMaterial: (...args: unknown[]) => unknown;
  };
}

declare module "*/gvrm-vendor/gvrm-format/utils.js" {
  export const BONE_CONFIG: Record<string, { names: string[]; radius: number; scale: { x: number; z: number } }>;
  export function setPose(character: unknown, ops: unknown[]): void;
  export function getPointsMeshCapsules(character: unknown): unknown;
  export function visualizePMC(pmc: unknown, flag: boolean | null): void;
  export function addPMC(scene: unknown, pmc: unknown): void;
  export function removePMC(scene: unknown, pmc: unknown): void;
}

declare module "*/gvrm-vendor/gvrm-format/vrm.js" {
  export class VRMCharacter {
    constructor(...args: unknown[]);
  }
  export function loadMixamoAnimation(url: string, vrm: unknown, scale: number): Promise<unknown>;
}

declare module "*/gvrm-vendor/gvrm-format/ply.js" {
  export class PLYParser {
    parsePLY(path: string, includeColors?: boolean): Promise<unknown>;
    splitPLY(path: string, indices: unknown): Promise<string[]>;
  }
}

declare module "*/gvrm-vendor/gvrm-format/gs.js" {
  export class GaussianSplatting {
    constructor(...args: unknown[]);
  }
}
