// Minimal stand-ins for the Unity / Cesium API surface used by GeoCache, ZoneTool and ElevationLookup: type-checks them without Unity.
using System;
using System.Collections;
namespace Unity.Mathematics { public struct double3 { public double x, y, z; public double3(double a, double b, double c) { x = a; y = b; z = c; }
    public static double3 operator +(double3 a, double3 b) => new double3(a.x + b.x, a.y + b.y, a.z + b.z); public static double3 operator *(double3 a, double b) => new double3(a.x * b, a.y * b, a.z * b); }
  public static class math { public static double3 normalize(double3 v) => v; } }
namespace CesiumForUnity { public class CesiumGeoreference { public Unity.Mathematics.double3 TransformUnityPositionToEarthCenteredEarthFixed(Unity.Mathematics.double3 p) => p;
    public Unity.Mathematics.double3 TransformUnityDirectionToEarthCenteredEarthFixed(Unity.Mathematics.double3 p) => p; public Unity.Mathematics.double3 TransformEarthCenteredEarthFixedPositionToUnity(Unity.Mathematics.double3 p) => p; } }
namespace UnityEngine {
  public struct Vector2 { public float x, y; public Vector2(float a, float b) { x = a; y = b; } public float sqrMagnitude => x * x + y * y; public static Vector2 zero => default; public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y); public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y); }
  public struct Vector3 { public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; } public static Vector3 up => new Vector3(0, 1, 0); public static Vector3 down => new Vector3(0, -1, 0);
    public static Vector3 operator +(Vector3 a, Vector3 b) => a; public static Vector3 operator *(Vector3 a, float b) => a; public static float Distance(Vector3 a, Vector3 b) => 0; }
  public struct Color { }
  public struct Rect { public Rect(float a, float b, float c, float d) { } public float x, y, width, height; public bool Contains(Vector2 p) => true; }
  public struct Matrix4x4 { }
  public struct Ray { public Vector3 origin, direction; }
  public struct RaycastHit { public Vector3 point; }
  public enum TouchPhase { Began, Moved, Ended }
  public struct Touch { public Vector2 position; public TouchPhase phase; }
  public enum TextAnchor { MiddleLeft }
  public static class Mathf { public static float Clamp(float v, float a, float b) => v; public static float Max(float a, float b) => a; public static float Min(float a, float b) => a; public static float Abs(float a) => a; }
  public static class Time { public static float unscaledTime; }
  public static class Screen { public static int width, height; }
  public static class Input { public static int touchCount; public static Touch GetTouch(int i) => default; public static Vector3 mousePosition; public static bool GetMouseButtonDown(int b) => false; public static bool GetMouseButtonUp(int b) => false; }
  public static class Physics { public static bool Raycast(Ray r, out RaycastHit h, float d) { h = default; return false; } public static bool Raycast(Vector3 o, Vector3 d, out RaycastHit h, float m) { h = default; return false; } }
  public static class JsonUtility { public static T FromJson<T>(string s) => default; }
  public static class Debug { }
  public class Object { public static void Destroy(Object o) { } }
  public class Shader { public static Shader Find(string n) => null; }
  public class Material : Object { public Material(Shader s) { } public int renderQueue; public string name; public Color color; public bool HasProperty(string n) => true; public void SetColor(string n, Color c) { } }
  public class GameObject : Object { public GameObject(string n) { } public T AddComponent<T>() where T : Component, new() => new T(); public Transform transform; }
  public class Component : Object { public Transform transform; public GameObject gameObject; }
  public class Transform : Component { public Vector3 position; public void SetParent(Transform t, bool b) { } }
  public class Behaviour : Component { public bool enabled; }
  public class Renderer : Component { public Material sharedMaterial; public UnityEngine.Rendering.ShadowCastingMode shadowCastingMode; public bool receiveShadows; public bool enabled; }
  public class LineRenderer : Renderer { public bool useWorldSpace, loop; public int positionCount, numCornerVertices; public float widthMultiplier; public void SetPositions(Vector3[] p) { } public void SetPosition(int i, Vector3 p) { } }
  public class Camera : Behaviour { public Ray ScreenPointToRay(Vector2 p) => default; }
  public class MonoBehaviour : Behaviour { public Coroutine StartCoroutine(IEnumerator e) => null; }
  public class Coroutine { }
  public class GUIStyle { public GUIStyle() { } public GUIStyle(GUIStyle o) { } public bool wordWrap; public int fontSize; public TextAnchor alignment; }
  public class GUISkin { public GUIStyle label, button, box; }
  public static class GUI { public static GUISkin skin; public static int depth; public static Matrix4x4 matrix; public static bool enabled; }
  public class GUILayoutOption { }
  public static class GUILayout { public static void BeginArea(Rect r, GUIStyle s) { } public static void EndArea() { } public static void BeginHorizontal() { } public static void EndHorizontal() { } public static void BeginVertical(GUIStyle s) { } public static void EndVertical() { }
    public static void Label(string t, GUIStyle s = null) { } public static void Label(string t, params GUILayoutOption[] o) { } public static bool Button(string t, params GUILayoutOption[] o) => false; public static bool Button(string t, GUIStyle s, params GUILayoutOption[] o) => false;
    public static GUILayoutOption Width(float w) => null; public static GUILayoutOption MinWidth(float w) => null; public static GUILayoutOption Height(float h) => null; public static string TextField(string t, params GUILayoutOption[] o) => t; public static string TextField(string t, int max, params GUILayoutOption[] o) => t;
    public static int Toolbar(int i, string[] s) => i; public static float HorizontalSlider(float v, float a, float b) => v; public static bool Toggle(bool v, string t) => v; public static void Space(float p) { }
    public static Vector2 BeginScrollView(Vector2 v) => v; public static void EndScrollView() { } }
  public static class GUIUtility { public static void ScaleAroundPivot(Vector2 s, Vector2 p) { } }
  public static class RuntimeSetup { }
}
namespace UnityEngine.Rendering { public enum ShadowCastingMode { Off } }
namespace UnityEngine.Networking { public static class UnityWebRequestX { } public class UnityWebRequest : IDisposable { public enum Result { Success } public Result result; public DownloadHandler downloadHandler; public static UnityWebRequest Get(string u) => null; public AsyncOp SendWebRequest() => null; public void Dispose() { } }
  public class DownloadHandler { public string text; } public class AsyncOp : IEnumerator { public object Current => null; public bool MoveNext() => false; public void Reset() { } } }
namespace RocketWorld {
  public static class UiTheme { public static readonly UnityEngine.Color Accent = default, Hot = default; public static UnityEngine.GUISkin Skin; public static UnityEngine.GUIStyle Title; }
  public class GameSettings { public double lat, lon; public float groundHeight; }
  public class DebugHud : UnityEngine.MonoBehaviour { public bool Over(UnityEngine.Vector2 p) => false; }
  public class GameRoot : UnityEngine.MonoBehaviour { public UnityEngine.Camera cam; public CesiumForUnity.CesiumGeoreference georeference; public GameSettings settings; public DebugHud hud; public void TeleportTo(double lat, double lon, float gh) { } }
}
