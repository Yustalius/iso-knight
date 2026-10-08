import * as THREE from 'three';
import { mergeGeometries, toCreasedNormals } from 'three/addons/utils/BufferGeometryUtils.js';

// Construction kit for rigid-skinned low-poly characters (knight, soldier): bones, primitive
// pieces attached to bones, and a bake step that merges everything into one SkinnedMesh.
// Every vertex belongs to exactly one bone, one draw call per material.

export function createKit(root) {
  const bones = [], joints = {}, pieces = [];
  function joint(name, parent, x, y, z) {
    const b = new THREE.Bone(); b.name = name; b.position.set(x, y, z); parent.add(b);
    bones.push(b); joints[name] = b; return b;
  }
  function piece(name, geometry, mat, bone, pos = [0,0,0], rot = [0,0,0]) {
    const mesh = new THREE.Mesh(geometry, mat); mesh.name = name; mesh.position.set(...pos); mesh.rotation.set(...rot);
    bone.add(mesh); pieces.push({ mesh, bone }); return mesh;
  }
  const box = (name, size, mat, bone, pos, rot) => piece(name, new THREE.BoxGeometry(...size), mat, bone, pos, rot);
  function ellipsoid(name, radii, mat, bone, pos, segments = 8) {
    const g = new THREE.SphereGeometry(1, segments, 5); g.scale(...radii); return piece(name, g, mat, bone, pos);
  }
  // Chamfered rings keep a human silhouette at small on-screen size.
  function rings(name, levels, mat, bone, pos = [0,0,0], segments = 8, rot) {
    const p = [], uv = [], idx = [];
    levels.forEach(([y, rx, rz, zc = 0], j) => {
      for (let i = 0; i <= segments; i++) {
        const a = (i/segments)*Math.PI*2 + Math.PI/8;
        p.push(Math.cos(a)*rx, y, Math.sin(a)*rz + zc); uv.push(i/segments, j/(levels.length-1));
      }
    });
    for (let j = 0; j < levels.length-1; j++) for (let i = 0; i < segments; i++) {
      const a = j*(segments+1) + i, b = a + segments + 1; idx.push(a, b, a+1, b, b+1, a+1);
    }
    for (let i = 1; i < segments-1; i++) { idx.push(0, i, i+1); const k = (levels.length-1)*(segments+1); idx.push(k, k+i+1, k+i); }
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(p, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2)); g.setIndex(idx); g.computeVertexNormals();
    return piece(name, g.toNonIndexed(), mat, bone, pos, rot);
  }
  // Extruded 2-D outline (x, y), `depth` along +Z.
  function panel(name, points, depth, mat, bone, pos = [0,0,0], bevel = .007, rot) {
    const shape = new THREE.Shape(points.map(p => new THREE.Vector2(...p)));
    const g = new THREE.ExtrudeGeometry(shape, { depth, bevelEnabled: bevel > 0, bevelThickness: bevel, bevelSize: bevel, bevelSegments: 1, steps: 1, curveSegments: 1 });
    const p = g.attributes.position, u = g.attributes.uv;
    for (let i = 0; i < p.count; i++) u.setXY(i, p.getX(i)*1.7 + .5, p.getY(i)*1.7 + .5);
    return piece(name, g, mat, bone, pos, rot);
  }
  // Side profile (z, y) extruded across X and centred on x = 0: rifle parts, boots, pouches.
  function profile(name, points, width, mat, bone, pos = [0,0,0], bevel = .003, rot = [0,0,0]) {
    const m = panel(name, points, width, mat, bone, [0,0,0], bevel);
    m.geometry.translate(0, 0, -width/2); m.geometry.rotateY(-Math.PI/2);
    m.position.set(...pos); m.rotation.set(...rot); return m;
  }

  // `crease` (radians) smooths normals across edges shallower than that angle: rounded parts shade
  // smoothly while boxes keep crisp edges. Without it every face is flat, as the knight is drawn.
  function bake(name, { crease = 0 } = {}) {
    root.updateMatrixWorld(true);
    const buckets = new Map();
    for (const { mesh, bone } of pieces) {
      let g = mesh.geometry.index ? mesh.geometry.toNonIndexed() : mesh.geometry.clone();
      g.applyMatrix4(mesh.matrixWorld);
      g.deleteAttribute('normal');
      if (crease) g = toCreasedNormals(g, crease); else g.computeVertexNormals();
      const count = g.attributes.position.count, ids = new Uint16Array(count*4), weights = new Float32Array(count*4);
      const bi = bones.indexOf(bone);
      for (let i = 0; i < count; i++) { ids[i*4] = bi; weights[i*4] = 1; }
      g.setAttribute('skinIndex', new THREE.Uint16BufferAttribute(ids, 4)); g.setAttribute('skinWeight', new THREE.Float32BufferAttribute(weights, 4));
      if (!buckets.has(mesh.material)) buckets.set(mesh.material, []);
      buckets.get(mesh.material).push(g); bone.remove(mesh);
    }
    const materials = [...buckets.keys()], merged = materials.map(mat => mergeGeometries(buckets.get(mat)));
    const geometry = mergeGeometries(merged, true);
    const skin = new THREE.SkinnedMesh(geometry, materials); skin.name = name;
    skin.castShadow = true; skin.receiveShadow = true; skin.frustumCulled = false;
    root.add(skin); root.updateMatrixWorld(true); skin.bind(new THREE.Skeleton(bones));
    return { skin, geometry };
  }
  return { bones, joints, pieces, joint, piece, box, ellipsoid, rings, panel, profile, bake };
}
