using EFT.CameraControl;
using UnityEngine;

namespace SPTPracticeMenu
{
    public sealed partial class Plugin
    {
        private bool opticProjectionActive;
        private Renderer cachedLens;
        private bool lensHasUvMapping;
        private Vector3 lensLocalCenter, lensLocalU, lensLocalV;
        private Vector3 lensCenter, lensU, lensV;
        private Matrix4x4 opticViewProjection;
        private Vector2 lensScreenCenter, lensScreenU, lensScreenV;
        private float lensScreenArea;
        private Camera renderedMainCamera;
        private int renderedMainFrame = -1;
        private Matrix4x4 renderedMainViewProjection;
        private Rect renderedMainViewport;

        private void CaptureMainProjection(Camera renderingCamera)
        {
            if (suppressed || renderingCamera != camera) return;

            // Camera mods can move the camera in pre-cull and restore it after rendering.
            renderedMainViewProjection = renderingCamera.projectionMatrix * renderingCamera.worldToCameraMatrix;
            renderedMainViewport = renderingCamera.rect;
            renderedMainCamera = renderingCamera;
            renderedMainFrame = Time.frameCount;
        }

        private static bool TryRenderedViewportPoint(Matrix4x4 viewProjection, Vector3 point, out Vector2 viewportPoint)
        {
            viewportPoint = default(Vector2);
            Vector4 clip = viewProjection * new Vector4(point.x, point.y, point.z, 1);
            if (!(clip.w > 0)) return false;
            viewportPoint = new Vector2(0.5f + 0.5f * clip.x / clip.w, 0.5f + 0.5f * clip.y / clip.w);
            return viewportPoint.x >= 0 && viewportPoint.x <= 1 && viewportPoint.y >= 0 && viewportPoint.y <= 1;
        }

        private void UpdateOpticProjection()
        {
            bool wasActive = opticProjectionActive;
            opticProjectionActive = false;
            var manager = CameraManager.Exist ? CameraManager.Instance.OpticCameraManager : null;
            var sight = manager != null ? manager.CurrentOpticSight : null;
            var optic = sight != null ? manager.Camera : null;
            var lens = sight != null ? sight.LensRenderer : null;
            if (optic != null && optic.isActiveAndEnabled && optic.targetTexture != null &&
                lens != null && lens.gameObject.activeInHierarchy)
            {
                if (cachedLens != lens)
                {
                    cachedLens = lens;
                    lensHasUvMapping = ReadLensMapping(lens);
                    nextGroups = 0;
                }

                Transform lensTransform = lens.transform;
                if (lensHasUvMapping)
                {
                    lensCenter = lensTransform.TransformPoint(lensLocalCenter);
                    lensU = lensTransform.TransformVector(lensLocalU);
                    lensV = lensTransform.TransformVector(lensLocalV);
                }
                else
                {
                    // Some lens meshes are GPU-only. Use the lens bounds and the optic's axes.
                    Bounds bounds = lens.localBounds;
                    lensCenter = lensTransform.TransformPoint(bounds.center);
                    lensU = LensBoundsAxis(lensTransform, bounds.extents, optic.transform.right);
                    lensV = LensBoundsAxis(lensTransform, bounds.extents, optic.transform.up);
                }

                Vector2 edgeU, edgeV;
                if (MainScreenPoint(lensCenter, out lensScreenCenter) &&
                    MainScreenPoint(lensCenter + lensU * 0.5f, out edgeU) &&
                    MainScreenPoint(lensCenter + lensV * 0.5f, out edgeV))
                {
                    lensScreenU = edgeU - lensScreenCenter;
                    lensScreenV = edgeV - lensScreenCenter;
                    lensScreenArea = Cross(lensScreenU, lensScreenV);
                    opticProjectionActive = Mathf.Abs(lensScreenArea) > 1;
                    opticViewProjection = optic.nonJitteredProjectionMatrix * optic.worldToCameraMatrix;
                }
            }
            if (wasActive != opticProjectionActive) nextGroups = 0;
        }

        private static Vector3 LensBoundsAxis(Transform lens, Vector3 extents, Vector3 axis)
        {
            float radius = Mathf.Abs(Vector3.Dot(lens.TransformVector(Vector3.right * extents.x), axis)) +
                Mathf.Abs(Vector3.Dot(lens.TransformVector(Vector3.up * extents.y), axis)) +
                Mathf.Abs(Vector3.Dot(lens.TransformVector(Vector3.forward * extents.z), axis));
            return axis * (2 * radius);
        }

        private bool ReadLensMapping(Renderer lens)
        {
            var skinned = lens as SkinnedMeshRenderer;
            var filter = skinned == null ? lens.GetComponent<MeshFilter>() : null;
            var mesh = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable || !mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))
                return false;
            return TryLensMapping(mesh.uv, mesh.vertices, out lensLocalCenter, out lensLocalU, out lensLocalV);
        }

        private static bool TryLensMapping(Vector2[] uv, Vector3[] vertices,
            out Vector3 center, out Vector3 axisU, out Vector3 axisV)
        {
            center = axisU = axisV = Vector3.zero;
            if (uv.Length < 3 || vertices.Length != uv.Length) return false;

            // Use a wide UV triangle to recover the planar mapping from texture to lens.
            int left = 0, right = 0, third = 0;
            for (int i = 1; i < uv.Length; i++)
            {
                if (uv[i].x < uv[left].x) left = i;
                if (uv[i].x > uv[right].x) right = i;
            }
            Vector2 a = uv[right] - uv[left];
            float area = 0;
            for (int i = 0; i < uv.Length; i++)
            {
                float candidate = Cross(a, uv[i] - uv[left]);
                if (Mathf.Abs(candidate) > Mathf.Abs(area)) { area = candidate; third = i; }
            }
            if (Mathf.Abs(area) < 0.0001f) return false;

            Vector2 b = uv[third] - uv[left];
            Vector3 edgeA = vertices[right] - vertices[left], edgeB = vertices[third] - vertices[left];
            axisU = (edgeA * b.y - edgeB * a.y) / area;
            axisV = (edgeB * a.x - edgeA * b.x) / area;
            center = vertices[left] + axisU * (0.5f - uv[left].x) + axisV * (0.5f - uv[left].y);
            return true;
        }

        private static float Cross(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }

        private bool ScreenPoint(Vector3 point, out Vector2 screen)
        {
            bool throughOptic;
            return ScreenPoint(point, out screen, out throughOptic);
        }

        private bool ScreenPoint(Vector3 point, out Vector2 screen, out bool throughOptic)
        {
            throughOptic = false;
            if (!opticProjectionActive) return MainScreenPoint(point, out screen);
            if (OpticScreenPoint(point, out screen))
            {
                throughOptic = true;
                return true;
            }
            if (!MainScreenPoint(point, out screen)) return false;
            // The main camera's image is covered by the lens at this location.
            Vector2 delta = screen - lensScreenCenter;
            float x = Cross(delta, lensScreenV) / lensScreenArea;
            float y = Cross(lensScreenU, delta) / lensScreenArea;
            return x * x + y * y > 1;
        }

        private bool ProjectEspPoint(Vector3 point, bool throughOptic, out Vector2 screen)
        {
            return throughOptic ? OpticScreenPoint(point, out screen) : MainScreenPoint(point, out screen);
        }

        private bool OpticScreenPoint(Vector3 point, out Vector2 screen)
        {
            screen = default(Vector2);
            Vector2 offset;
            if (!TryOpticOffset(opticViewProjection, point, out offset)) return false;
            // The optic renders to a square texture sampled on the moving lens mesh.
            return MainScreenPoint(lensCenter + lensU * offset.x + lensV * offset.y, out screen);
        }

        private static bool TryOpticOffset(Matrix4x4 viewProjection, Vector3 point, out Vector2 offset)
        {
            offset = default(Vector2);
            Vector4 clip = viewProjection * new Vector4(point.x, point.y, point.z, 1);
            if (!(clip.w > 0)) return false;
            offset = new Vector2(clip.x / (2 * clip.w), clip.y / (2 * clip.w));
            return offset.sqrMagnitude <= 0.25f;
        }
    }
}
