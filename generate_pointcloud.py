#!/usr/bin/env python3
# Must be set before cv2/imageio are imported so OpenCV loads OpenEXR support
import os
os.environ["OPENCV_IO_ENABLE_OPENEXR"] = "1"

"""
Generate a merged colored PLY point cloud from all keyframes saved by KeyFrameManager.

Usage:
  pip install numpy imageio
  python generate_pointcloud.py <keyframes_root> [output.ply]

  Example:
    python generate_pointcloud.py C:\\Users\\danie\\Desktop\\keyframes output.ply
"""

import json
import sys
import numpy as np

try:
    import imageio.v3 as iio
except ImportError:
    import imageio as iio

import cv2


def read_exr_r(path):
    """Read the R channel of an EXR as float32.

    imageio decodes these float EXRs as uint8 garbage; OpenCV (with
    OPENCV_IO_ENABLE_OPENEXR=1) reads them correctly as float32.
    """
    img = cv2.imread(path, cv2.IMREAD_UNCHANGED)
    if img is None:
        raise IOError(f"cv2 could not read EXR: {path}")
    if img.ndim == 3:
        img = img[:, :, 0]  # R=G=B for our depth EXR; channel 0 is fine
    return img.astype(np.float32)


def meta_depth_sample(raw):
    """Return Meta's faithful depth-buffer sample from the saved raw value.

    Meta's _EnvironmentDepthTexture is a NON-reversed depth buffer: raw=0 near,
    raw=1 far (see EnvironmentOcclusion.cginc). A faithful capture of a real scene
    therefore has a HIGH median (most pixels are far walls/floor). If the saved
    median is low, the capture stored 1-raw (e.g. it used the DepthPreview
    '1.0 - depth' material instead of DepthRawCopy) — undo that inversion here so
    the official linearization applies in both cases.
    """
    raw = np.asarray(raw, dtype=np.float64)
    has_data = raw > 0.001
    med = np.median(raw[has_data]) if np.any(has_data) else 1.0
    return (1.0 - raw) if med < 0.5 else raw


def quaternion_to_rotation_matrix(qx, qy, qz, qw):
    """Unity quaternion → 3x3 rotation matrix (camera-local → world)."""
    return np.array([
        [1 - 2*(qy*qy + qz*qz),     2*(qx*qy - qz*qw),     2*(qx*qz + qy*qw)],
        [    2*(qx*qy + qz*qw), 1 - 2*(qx*qx + qz*qz),     2*(qy*qz - qx*qw)],
        [    2*(qx*qz - qy*qw),     2*(qy*qz + qx*qw), 1 - 2*(qx*qx + qy*qy)],
    ], dtype=np.float64)


def parse_unity_matrix4x4(data):
    """Parse Unity Matrix4x4 JSON -> numpy 4x4 array.

    Unity's JsonUtility serializes Matrix4x4 with fields named mRC where
    mRowCol means row R, column C (e.g. m01 = row 0, column 1).
    """
    return np.array([
        [data['m00'], data['m01'], data['m02'], data['m03']],
        [data['m10'], data['m11'], data['m12'], data['m13']],
        [data['m20'], data['m21'], data['m22'], data['m23']],
        [data['m30'], data['m31'], data['m32'], data['m33']],
    ], dtype=np.float64)


def process_keyframe_direct(keyframe_dir, min_depth=0.1, max_depth=8.0, edge_thresh=0.05,
                            border=6, max_grazing_deg=80.0):
    """
    Direct 3D unprojection: raw depth -> world points -> RGB color lookup.
    No iterative shader registration needed.

    Returns (N,3) float32 world points + (N,3) uint8 colors.
    """
    # --- Load data ---
    raw_depth = read_exr_r(os.path.join(keyframe_dir, 'rawDepth.exr'))
    raw_depth = raw_depth[::-1, :]  # Unity EXR is bottom-up

    rgb = iio.imread(os.path.join(keyframe_dir, 'LeftRGB.png'))
    rgb = rgb[::-1, :, :]  # Match Y-flip
    H_rgb, W_rgb = rgb.shape[:2]

    with open(os.path.join(keyframe_dir, 'LeftCamPose.json')) as f:
        pose = json.load(f)
    with open(os.path.join(keyframe_dir, 'LeftIntrinsics.json')) as f:
        intr = json.load(f)
    with open(os.path.join(keyframe_dir, 'reprojection.json')) as f:
        reproj_data = json.load(f)
    with open(os.path.join(keyframe_dir, 'zbuffer_params.json')) as f:
        zbuf = json.load(f)

    # --- Parse parameters ---
    M = parse_unity_matrix4x4(reproj_data)
    inv_M = np.linalg.inv(M)

    zx, zy = float(zbuf['x']), float(zbuf['y'])
    if zy == 0:
        raise ValueError("zBufferParams.y == 0, cannot linearize depth")

    fx = float(intr['FocalLength']['x'])
    fy = float(intr['FocalLength']['y'])
    cx = float(intr['PrincipalPoint']['x'])
    cy = float(intr['PrincipalPoint']['y'])
    sx = float(intr['SensorResolution']['x'])
    sy = float(intr['SensorResolution']['y'])

    rgb_pos = np.array([pose['px'], pose['py'], pose['pz']], dtype=np.float64)
    R_rgb = quaternion_to_rotation_matrix(pose['rx'], pose['ry'], pose['rz'], pose['rw'])

    # --- RGB crop region (mirrors SDK's CalcSensorCropRegion) ---
    scale_x = W_rgb / sx
    scale_y = H_rgb / sy
    max_s = max(scale_x, scale_y)
    scale_x /= max_s
    scale_y /= max_s
    crop_x = sx * (1.0 - scale_x) * 0.5
    crop_y = sy * (1.0 - scale_y) * 0.5
    crop_w = sx * scale_x
    crop_h = sy * scale_y

    # --- Vectorized depth unprojection ---
    # M maps world -> depth clip space. Using Meta's official convention
    # (EnvironmentOcclusion.cginc): the faithful sample m gives NDC z = 2m-1 and
    # linear depth = zx/((2m-1)+zy). Pushing (ndc_x, ndc_y, 2m-1) through inv(M)
    # and dehomogenizing yields the world point directly — no ray scaling needed.
    # Verified geometrically on real captures (planar floor ~3cm, room height ~3m).
    H_d, W_d = raw_depth.shape
    u_coords = (np.arange(W_d, dtype=np.float64) + 0.5) / W_d
    v_coords = (np.arange(H_d, dtype=np.float64) + 0.5) / H_d
    uu, vv = np.meshgrid(u_coords, v_coords)

    ndc_x = (uu * 2 - 1).ravel()
    ndc_y = (vv * 2 - 1).ravel()
    raw_flat = raw_depth.ravel().astype(np.float64)
    N = ndc_x.size

    m = meta_depth_sample(raw_flat)            # faithful Meta sample (un-inverted)
    ndc_z = 2.0 * m - 1.0                       # official Meta NDC z (cginc)
    denom = ndc_z + zy
    linear_z = np.full(N, np.inf, dtype=np.float64)
    nz = np.abs(denom) > 1e-9
    linear_z[nz] = zx / denom[nz]              # official linearization: zx/((2m-1)+zy)

    valid = (raw_flat > 0.001) & (linear_z > min_depth) & (linear_z < max_depth)

    # Depth-discontinuity (edge) filter: drop "flying pixels" at depth jumps.
    # A pixel straddling a foreground/background edge linearizes to a mid value and
    # unprojects to a point floating in mid-air along the ray ("comet tails").
    # Reject a pixel if its linear depth differs from a 4-neighbour by more than
    # edge_thresh (relative). edge_thresh<=0 disables the filter.
    if edge_thresh and edge_thresh > 0:
        lz = linear_z.reshape(H_d, W_d)
        lz_safe = np.where(np.isfinite(lz), lz, 0.0)
        jump = np.zeros((H_d, W_d), dtype=np.float64)
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            shifted = np.roll(lz_safe, (di, dj), axis=(0, 1))
            jump = np.maximum(jump, np.abs(lz_safe - shifted))
        edge_ok = (jump < edge_thresh * lz_safe).ravel()
        valid &= edge_ok

    # Border crop: the outermost depth pixels are unreliable (wide depth FOV,
    # extrapolated edges) and produce the draping "curtains" at grazing angles.
    if border and border > 0:
        bmask = np.zeros((H_d, W_d), dtype=bool)
        bmask[border:H_d - border, border:W_d - border] = True
        valid &= bmask.ravel()

    # Unproject the full NDC point through inv(M)
    ones = np.ones(N, dtype=np.float64)
    clip = np.stack([ndc_x, ndc_y, ndc_z, ones], axis=0)   # 4xN
    world_h = inv_M @ clip                                   # 4xN
    valid &= np.abs(world_h[3]) > 1e-9
    with np.errstate(divide='ignore', invalid='ignore'):
        world_pts = (world_h[:3] / world_h[3:4]).T          # Nx3

    # Grazing-angle filter: surfaces seen nearly edge-on (walls/floor viewed along
    # their plane) "stretch" into dense sheets/skirts that survive the voxel filter.
    # Estimate each pixel's surface normal from its 3D neighbours and drop points
    # whose normal is more than max_grazing_deg away from the viewing ray.
    if max_grazing_deg and max_grazing_deg < 90:
        Wp = world_pts.reshape(H_d, W_d, 3)
        tx = np.zeros_like(Wp); tx[:, :-1] = Wp[:, 1:] - Wp[:, :-1]
        ty = np.zeros_like(Wp); ty[:-1] = Wp[1:] - Wp[:-1]
        nrm = np.cross(tx, ty)
        nrm /= (np.linalg.norm(nrm, axis=2, keepdims=True) + 1e-12)
        ray = Wp - rgb_pos
        ray /= (np.linalg.norm(ray, axis=2, keepdims=True) + 1e-12)
        cosang = np.abs(np.sum(nrm * ray, axis=2))          # 1=facing, 0=edge-on
        graze_ok = (cosang > np.cos(np.radians(max_grazing_deg))).ravel()
        valid &= np.nan_to_num(graze_ok.astype(float), nan=0.0).astype(bool)
    world_pts = world_pts[valid]

    # --- Project into RGB camera ---
    p_local = (R_rgb.T @ (world_pts - rgb_pos).T)  # 3 x N_valid

    in_front = p_local[2] > 0.01
    p_local = p_local[:, in_front]
    world_pts = world_pts[in_front]

    # Pinhole projection
    sensor_x = (p_local[0] / p_local[2]) * fx + cx
    sensor_y = (p_local[1] / p_local[2]) * fy + cy

    # Sensor -> image UV via crop
    u_rgb = (sensor_x - crop_x) / crop_w
    v_rgb = (sensor_y - crop_y) / crop_h

    # Bounds check
    in_bounds = (u_rgb >= 0) & (u_rgb < 1) & (v_rgb >= 0) & (v_rgb < 1)
    u_rgb = u_rgb[in_bounds]
    v_rgb = v_rgb[in_bounds]
    world_pts = world_pts[in_bounds]

    # Sample RGB (nearest neighbor)
    px_x = np.clip((u_rgb * W_rgb).astype(int), 0, W_rgb - 1)
    px_y = np.clip((v_rgb * H_rgb).astype(int), 0, H_rgb - 1)
    colors = rgb[px_y, px_x, :3]

    return world_pts.astype(np.float32), colors.astype(np.uint8)


def process_keyframe(keyframe_dir, min_depth=0.1, max_depth=15.0):
    """Load one keyframe and return (N,3) world points + (N,3) uint8 colors."""
    depth = read_exr_r(os.path.join(keyframe_dir, 'depth.exr'))
    rgb   = iio.imread(os.path.join(keyframe_dir, 'LeftRGB.png'))

    with open(os.path.join(keyframe_dir, 'intrinsics.json')) as f:
        intr = json.load(f)
    with open(os.path.join(keyframe_dir, 'pose.json')) as f:
        pose = json.load(f)

    H, W = depth.shape

    # Unity EncodeToEXR stores Y=0 at the bottom; cv2 reads row-0 at top → flip
    depth = depth[::-1, :]
    rgb   = rgb  [::-1, :, :]

    # Intrinsics (sensor pixel space, Y-up convention)
    fx = float(intr['FocalLength']['x'])
    fy = float(intr['FocalLength']['y'])
    cx = float(intr['PrincipalPoint']['x'])
    cy = float(intr['PrincipalPoint']['y'])
    sx = float(intr['SensorResolution']['x'])
    sy = float(intr['SensorResolution']['y'])

    # Camera pose
    pos = np.array([pose['px'], pose['py'], pose['pz']], dtype=np.float64)
    R   = quaternion_to_rotation_matrix(pose['rx'], pose['ry'], pose['rz'], pose['rw'])

    # Compute crop region (mirrors SDK's CalcSensorCropRegion).
    # Quest 3: sensor=1280×1280, camera at 1280×960 → cropY=160, cropH=960.
    # Without this, sensor_y spans [0,1280] instead of [160,1120] — ~10° error at edges.
    scale_x = W / sx
    scale_y = H / sy
    max_scale = max(scale_x, scale_y)
    scale_x /= max_scale
    scale_y /= max_scale
    crop_x = sx * (1.0 - scale_x) * 0.5
    crop_y = sy * (1.0 - scale_y) * 0.5
    crop_w = sx * scale_x
    crop_h = sy * scale_y

    # Pixel grid → sensor coords (with crop offset) → ray direction
    col_grid, row_grid = np.meshgrid(np.arange(W, dtype=np.float32),
                                     np.arange(H, dtype=np.float32))
    sensor_x = crop_x + (col_grid / W) * crop_w
    sensor_y = crop_y + (row_grid / H) * crop_h

    dx = (sensor_x - cx) / fx
    dy = (sensor_y - cy) / fy

    t = depth.ravel()
    Px = t * dx.ravel()
    Py = t * dy.ravel()
    Pz = t

    valid = (Pz > min_depth) & (Pz < max_depth)
    P_cam = np.stack([Px, Py, Pz], axis=1)[valid]
    colors = rgb[:, :, :3].reshape(-1, 3)[valid]

    P_world = (R @ P_cam.T).T + pos
    return P_world.astype(np.float32), colors.astype(np.uint8)


def voxel_downsample(points, colors, voxel=0.02, min_pts=2):
    """Average points/colors into a voxel grid; drop voxels with < min_pts points.

    Merges redundant overlapping samples from many keyframes (removes "doubled"
    surfaces) and removes sparse floating noise / depth-edge curtains, which land
    in voxels hit by very few points.
    """
    if len(points) == 0 or voxel <= 0:
        return points, colors
    keys = np.floor(points / voxel).astype(np.int64)
    order = np.lexsort((keys[:, 2], keys[:, 1], keys[:, 0]))
    ks, ps, cs = keys[order], points[order], colors[order].astype(np.float64)
    new_group = np.empty(len(ks), dtype=bool)
    new_group[0] = True
    np.any(ks[1:] != ks[:-1], axis=1, out=new_group[1:])
    starts = np.nonzero(new_group)[0]
    counts = np.diff(np.append(starts, len(ks)))
    pmean = np.add.reduceat(ps, starts, axis=0) / counts[:, None]
    cmean = np.add.reduceat(cs, starts, axis=0) / counts[:, None]
    keep = counts >= min_pts
    return pmean[keep].astype(np.float32), np.clip(cmean[keep], 0, 255).astype(np.uint8)


def write_ply(output_path, all_points, all_colors):
    n = len(all_points)
    header = (
        "ply\n"
        "format binary_little_endian 1.0\n"
        f"element vertex {n}\n"
        "property float x\n"
        "property float y\n"
        "property float z\n"
        "property uchar red\n"
        "property uchar green\n"
        "property uchar blue\n"
        "end_header\n"
    ).encode()

    dtype = np.dtype([
        ('x', '<f4'), ('y', '<f4'), ('z', '<f4'),
        ('r', 'u1'),  ('g', 'u1'),  ('b', 'u1'),
    ])
    verts = np.empty(n, dtype=dtype)
    verts['x'] = all_points[:, 0]
    verts['y'] = all_points[:, 1]
    verts['z'] = all_points[:, 2]
    verts['r'] = all_colors[:, 0]
    verts['g'] = all_colors[:, 1]
    verts['b'] = all_colors[:, 2]

    with open(output_path, 'wb') as f:
        f.write(header)
        f.write(verts.tobytes())


def diag_keyframe(keyframe_dir):
    """Print depth and pose stats for one keyframe — no PLY output."""
    depth = iio.imread(os.path.join(keyframe_dir, 'depth.exr'))
    if depth.ndim == 3:
        depth = depth[:, :, 0]
    depth = depth.astype(np.float32)

    with open(os.path.join(keyframe_dir, 'pose.json')) as f:
        pose = json.load(f)
    with open(os.path.join(keyframe_dir, 'intrinsics.json')) as f:
        intr = json.load(f)

    valid = depth[(depth > 0.01) & (depth < 50.0)]
    name  = os.path.basename(keyframe_dir)

    print(f"\n── Keyframe {name} ──────────────────────────")
    if valid.size == 0:
        print("  depth: ALL ZERO or invalid — registration produced no output")
    else:
        print(f"  depth (valid px): min={valid.min():.3f}m  mean={valid.mean():.3f}m  "
              f"max={valid.max():.3f}m  coverage={100*valid.size/depth.size:.1f}%")
    print(f"  pose:  pos=({pose['px']:.3f}, {pose['py']:.3f}, {pose['pz']:.3f})  "
          f"rot=({pose['rx']:.3f}, {pose['ry']:.3f}, {pose['rz']:.3f}, {pose['rw']:.3f})")
    print(f"  intr:  fx={intr['FocalLength']['x']:.1f}  fy={intr['FocalLength']['y']:.1f}  "
          f"cx={intr['PrincipalPoint']['x']:.1f}  cy={intr['PrincipalPoint']['y']:.1f}  "
          f"res={intr['SensorResolution']['x']}x{intr['SensorResolution']['y']}")


def diag_keyframe_direct(keyframe_dir):
    """Print diagnostics for the direct unprojection pipeline — no PLY output."""
    name = os.path.basename(keyframe_dir)
    print(f"\n── Keyframe {name} (direct) ─────────────────────")
    try:
        with open(os.path.join(keyframe_dir, 'reprojection.json')) as f:
            reproj_data = json.load(f)
        with open(os.path.join(keyframe_dir, 'zbuffer_params.json')) as f:
            zbuf = json.load(f)
        M = parse_unity_matrix4x4(reproj_data)
        inv_M = np.linalg.inv(M)
    except Exception as e:
        print(f"  ERROR loading matrices: {e}")
        return

    for r in range(4):
        print(f"  reproj row{r}: [{M[r,0]:+.4f} {M[r,1]:+.4f} {M[r,2]:+.4f} {M[r,3]:+.4f}]")

    # Depth-camera origin = unproject ndc (0,0) at the near plane (m=0 → ndc_z=-1)
    near_h = inv_M @ np.array([0, 0, -1, 1], dtype=np.float64)
    cam_origin = near_h[:3] / near_h[3]
    print(f"  cam_origin (near pt): ({cam_origin[0]:+.3f}, {cam_origin[1]:+.3f}, {cam_origin[2]:+.3f})")

    zx, zy = float(zbuf['x']), float(zbuf['y'])
    print(f"  zBufferParams: x={zx:.4f} y={zy:.4f} z={zbuf['z']:.4f} w={zbuf['w']:.4f}")

    try:
        raw_depth = read_exr_r(os.path.join(keyframe_dir, 'rawDepth.exr')).astype(np.float64)
    except Exception as e:
        print(f"  ERROR loading rawDepth.exr: {e}")
        return

    valid_raw = raw_depth[raw_depth > 0.001]
    inverted = valid_raw.size > 0 and np.median(valid_raw) < 0.5
    print(f"  raw depth dims: {raw_depth.shape[1]}x{raw_depth.shape[0]}  "
          f"valid px: {valid_raw.size}  raw range: [{raw_depth.min():.4f}, {raw_depth.max():.4f}]"
          f"  {'(INVERTED capture -> auto-corrected)' if inverted else '(faithful)'}")
    if valid_raw.size == 0:
        print("  depth: no valid raw samples (rawDepth.exr is empty/uniform)")
    else:
        m = meta_depth_sample(valid_raw)
        denom = (2.0 * m - 1.0) + zy
        lin = zx / denom
        lin = lin[np.isfinite(lin) & (lin > 0)]
        print(f"  linearized depth: min={lin.min():.3f}m mean={lin.mean():.3f}m max={lin.max():.3f}m")

    try:
        pts, cols = process_keyframe_direct(keyframe_dir)
        total = raw_depth.size
        print(f"  colored points: {len(pts):,} / {total:,} depth px  ({100*len(pts)/total:.1f}% coverage)")
        if len(pts) < 0.1 * total:
            print("  WARNING: fewer than 10% of depth pixels produced colored points")
    except Exception as e:
        print(f"  ERROR in process_keyframe_direct: {e}")


def _pop_opt(args, name, cast, default):
    """Extract '--name value' from args list; return (remaining_args, value)."""
    if name in args:
        i = args.index(name)
        val = cast(args[i + 1])
        del args[i:i + 2]
        return args, val
    return args, default


if __name__ == '__main__':
    args = sys.argv[1:]
    diag_only = '--diag' in args
    registered = '--registered' in args
    args = [a for a in args if a not in ('--diag', '--registered', '--direct')]

    args, max_depth   = _pop_opt(args, '--max-depth', float, 8.0)
    args, edge_thresh = _pop_opt(args, '--edge',      float, 0.05)
    args, border      = _pop_opt(args, '--border',    int,   6)
    args, grazing     = _pop_opt(args, '--grazing',   float, 80.0)
    args, voxel       = _pop_opt(args, '--voxel',     float, 0.02)
    args, min_pts     = _pop_opt(args, '--min-pts',   int,   2)

    if not args:
        print(f"Usage: {os.path.basename(sys.argv[0])} <keyframes_root> [output.ply] [options]")
        print("  --diag             per-keyframe stats, skip PLY")
        print("  --registered       legacy pipeline (depth.exr)")
        print("  --max-depth M      cull points farther than M metres (default 8)")
        print("  --edge T           depth-discontinuity threshold, relative (default 0.05; 0=off)")
        print("  --border N         ignore outer N depth pixels (default 6)")
        print("  --grazing D        drop surfaces seen >D deg edge-on (default 80; 90=off)")
        print("  --voxel S          voxel size in metres for downsample/denoise (default 0.02; 0=off)")
        print("  --min-pts K        drop voxels with fewer than K points (default 2)")
        sys.exit(1)

    root    = args[0]
    out_ply = args[1] if len(args) > 1 else os.path.join(root, 'pointcloud.ply')

    marker = 'depth.exr' if registered else 'rawDepth.exr'

    # Collect all subdirectories that contain the required depth file
    kf_dirs = sorted(
        [os.path.join(root, d) for d in os.listdir(root)
         if os.path.isdir(os.path.join(root, d))
         and os.path.exists(os.path.join(root, d, marker))],
        key=lambda p: int(os.path.basename(p)) if os.path.basename(p).isdigit() else 0
    )

    if not kf_dirs:
        print(f"No keyframes (with {marker}) found in {root}")
        sys.exit(1)

    print(f"Found {len(kf_dirs)} keyframes  [pipeline: {'registered' if registered else 'direct'}]")

    if diag_only:
        for kf in kf_dirs:
            if registered:
                diag_keyframe(kf)
            else:
                diag_keyframe_direct(kf)
        sys.exit(0)

    all_pts, all_cols = [], []
    for kf in kf_dirs:
        try:
            if registered:
                pts, cols = process_keyframe(kf, max_depth=max_depth)
            else:
                pts, cols = process_keyframe_direct(kf, max_depth=max_depth,
                                                    edge_thresh=edge_thresh, border=border,
                                                    max_grazing_deg=grazing)
            all_pts.append(pts)
            all_cols.append(cols)
            print(f"  {os.path.basename(kf):>4s}  {len(pts):>8,} points")
        except Exception as e:
            print(f"  {os.path.basename(kf):>4s}  SKIP ({e})")

    if not all_pts:
        print("No valid keyframes processed.")
        sys.exit(1)

    merged_pts  = np.concatenate(all_pts,  axis=0)
    merged_cols = np.concatenate(all_cols, axis=0)
    raw_count = len(merged_pts)

    if voxel and voxel > 0:
        merged_pts, merged_cols = voxel_downsample(merged_pts, merged_cols, voxel, min_pts)
        print(f"\nVoxel downsample ({voxel} m, min {min_pts} pts): {raw_count:,} -> {len(merged_pts):,}")

    write_ply(out_ply, merged_pts, merged_cols)
    print(f"\nTotal: {len(merged_pts):,} points → {out_ply}")
