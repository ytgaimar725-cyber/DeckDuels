#region --- IMPORTS ---
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;
using SystemVector2 = System.Numerics.Vector2;
using SystemVector3 = System.Numerics.Vector3;
using SystemVector4 = System.Numerics.Vector4;
using SystemMatrix4 = System.Numerics.Matrix4x4;
#endregion

namespace DeckDuels
{
    #region --- SHADER SOURCES ---

    /// <summary>
    /// Inline GLSL shader source code for all render passes.
    /// </summary>
    public static class Shaders
    {
        public const string LitVertex = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vFragPos;
out vec3 vNormal;
out vec2 vTexCoord;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vFragPos = worldPos.xyz;
    vNormal = normalize(mat3(transpose(inverse(uModel))) * aNormal);
    vTexCoord = aTexCoord;
    gl_Position = uProjection * uView * worldPos;
}
";

        public const string LitFragment = @"
#version 330 core
in vec3 vFragPos;
in vec3 vNormal;
in vec2 vTexCoord;

uniform vec3 uLightPos;
uniform vec3 uLightColor;
uniform vec3 uViewPos;
uniform vec4 uBaseColor;
uniform sampler2D uTexture;
uniform float uUseTexture;
uniform float uEmissive;
uniform float uRimPower;
uniform vec3 uRimColor;
uniform float uTime;

out vec4 FragColor;

void main()
{
    vec3 norm = normalize(vNormal);
    vec3 lightDir = normalize(uLightPos - vFragPos);

    float diff = max(dot(norm, lightDir), 0.0);
    float dist = length(uLightPos - vFragPos);
    float attenuation = 1.0 / (1.0 + 0.045 * dist + 0.0075 * dist * dist);

    vec3 ambient = 0.18 * uLightColor;
    vec3 diffuse = diff * uLightColor * attenuation;

    vec3 viewDir = normalize(uViewPos - vFragPos);
    vec3 halfDir = normalize(lightDir + viewDir);
    float spec = pow(max(dot(norm, halfDir), 0.0), 64.0);
    vec3 specular = spec * uLightColor * attenuation * 0.6;

    vec3 rimDir = normalize(viewDir + vec3(0.0, 0.3, 0.0));
    float rim = 1.0 - max(dot(norm, rimDir), 0.0);
    rim = smoothstep(0.5, 1.0, rim) * uRimPower;
    vec3 rimLight = rim * uRimColor;

    vec4 texColor = uUseTexture > 0.5
        ? texture(uTexture, vTexCoord)
        : vec4(1.0);

    vec4 finalColor = texColor * uBaseColor;
    vec3 result = (ambient + diffuse + specular) * finalColor.rgb
                + uEmissive * finalColor.rgb
                + rimLight;

    FragColor = vec4(result, finalColor.a);
}
";

        public const string HUDVertex = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aTexCoord;

uniform mat4 uProjection;

out vec2 vTexCoord;

void main()
{
    vTexCoord = aTexCoord;
    gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
}
";

        public const string HUDFragment = @"
#version 330 core
in vec2 vTexCoord;

uniform sampler2D uTexture;
uniform vec4 uColor;
uniform float uUseTexture;
uniform float uTime;

out vec4 FragColor;

void main()
{
    vec4 texColor = uUseTexture > 0.5
        ? texture(uTexture, vTexCoord)
        : vec4(1.0);

    vec4 result = texColor * uColor;

    if (result.a < 0.01)
        discard;

    FragColor = result;
}
";

        public const string ParticleVertex = @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aVelocity;
layout(location = 2) in float aLife;
layout(location = 3) in float aSize;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uTime;

out float vLife;

void main()
{
    vec3 pos = aPosition;
    float t = uTime - 0.0;

    vLife = aLife;
    vec4 viewPos = uView * uModel * vec4(pos, 1.0);
    gl_Position = uProjection * viewPos;
    gl_PointSize = aSize * (300.0 / max(-viewPos.z, 0.1));
}
";

        public const string ParticleFragment = @"
#version 330 core
in float vLife;

uniform vec4 uColor;

out vec4 FragColor;

void main()
{
    vec2 coord = gl_PointCoord - vec2(0.5);
    float dist = length(coord);
    if (dist > 0.5)
        discard;

    float alpha = (1.0 - dist * 2.0) * clamp(vLife, 0.0, 1.0);
    FragColor = vec4(uColor.rgb, alpha * uColor.a);
}
";

        public const string PostProcessVertex = @"
#version 330 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aTexCoord;

out vec2 vTexCoord;

void main()
{
    vTexCoord = aTexCoord;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

        public const string PostProcessFragment = @"
#version 330 core
in vec2 vTexCoord;

uniform sampler2D uSceneTexture;
uniform float uTime;
uniform float uShakeIntensity;
uniform float uVignetteStrength;
uniform float uChromaticAberration;
uniform vec2 uScreenSize;

out vec4 FragColor;

void main()
{
    vec2 uv = vTexCoord;

    // Screen shake offset
    float shakeX = sin(uTime * 47.0) * uShakeIntensity * 0.01;
    float shakeY = cos(uTime * 53.0) * uShakeIntensity * 0.01;
    uv += vec2(shakeX, shakeY);

    // Chromatic aberration
    float ca = uChromaticAberration * 0.003;
    float r = texture(uSceneTexture, uv + vec2(ca, 0.0)).r;
    float g = texture(uSceneTexture, uv).g;
    float b = texture(uSceneTexture, uv - vec2(ca, 0.0)).b;
    float a = texture(uSceneTexture, uv).a;

    vec3 color = vec3(r, g, b);

    // Vignette
    vec2 center = uv - 0.5;
    float vig = 1.0 - dot(center, center) * uVignetteStrength;
    color *= clamp(vig, 0.0, 1.0);

    // Slight gamma correction
    color = pow(color, vec3(0.95));

    FragColor = vec4(color, a);
}
";
    }

    #endregion

    #region --- BITMAP FONT ---

    /// <summary>
    /// Simple 5x7 bitmap font for rendering text in OpenGL without external assets.
    /// </summary>
    public static class BitmapFont
    {
        public const int CharWidth = 5;
        public const int CharHeight = 7;

        static readonly Dictionary<char, string[]> _glyphs = new()
        {
            [' '] = new[] { "00000", "00000", "00000", "00000", "00000", "00000", "00000" },
            ['!'] = new[] { "00100", "00100", "00100", "00100", "00100", "00000", "00100" },
            ['"'] = new[] { "01010", "01010", "01010", "00000", "00000", "00000", "00000" },
            ['#'] = new[] { "01010", "01010", "11111", "01010", "11111", "01010", "01010" },
            ['$'] = new[] { "00100", "01111", "10100", "01110", "00101", "11110", "00100" },
            ['%'] = new[] { "11000", "11001", "00010", "00100", "01000", "10011", "00011" },
            ['&'] = new[] { "01100", "10010", "10100", "01000", "10101", "10010", "01101" },
            ['\''] = new[] { "00100", "00100", "00000", "00000", "00000", "00000", "00000" },
            ['('] = new[] { "00010", "00100", "01000", "01000", "01000", "00100", "00010" },
            [')'] = new[] { "01000", "00100", "00010", "00010", "00010", "00100", "01000" },
            ['*'] = new[] { "00000", "00100", "10101", "01110", "10101", "00100", "00000" },
            ['+'] = new[] { "00000", "00100", "00100", "11111", "00100", "00100", "00000" },
            [','] = new[] { "00000", "00000", "00000", "00000", "00100", "00100", "01000" },
            ['-'] = new[] { "00000", "00000", "00000", "11111", "00000", "00000", "00000" },
            ['.'] = new[] { "00000", "00000", "00000", "00000", "00000", "01100", "01100" },
            ['/'] = new[] { "00001", "00010", "00010", "00100", "01000", "01000", "10000" },
            ['0'] = new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" },
            ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
            ['2'] = new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" },
            ['3'] = new[] { "11111", "00010", "00100", "00010", "00001", "10001", "01110" },
            ['4'] = new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" },
            ['5'] = new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" },
            ['6'] = new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" },
            ['7'] = new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" },
            ['8'] = new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" },
            ['9'] = new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" },
            [':'] = new[] { "00000", "01100", "01100", "00000", "01100", "01100", "00000" },
            [';'] = new[] { "00000", "01100", "01100", "00000", "01100", "00100", "01000" },
            ['<'] = new[] { "00010", "00100", "01000", "10000", "01000", "00100", "00010" },
            ['='] = new[] { "00000", "00000", "11111", "00000", "11111", "00000", "00000" },
            ['>'] = new[] { "01000", "00100", "00010", "00001", "00010", "00100", "01000" },
            ['?'] = new[] { "01110", "10001", "00001", "00010", "00100", "00000", "00100" },
            ['@'] = new[] { "01110", "10001", "10111", "10101", "10111", "10000", "01110" },
            ['A'] = new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" },
            ['B'] = new[] { "11110", "10001", "10001", "11110", "10001", "10001", "11110" },
            ['C'] = new[] { "01110", "10001", "10000", "10000", "10000", "10001", "01110" },
            ['D'] = new[] { "11110", "10001", "10001", "10001", "10001", "10001", "11110" },
            ['E'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" },
            ['F'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "10000" },
            ['G'] = new[] { "01110", "10001", "10000", "10111", "10001", "10001", "01110" },
            ['H'] = new[] { "10001", "10001", "10001", "11111", "10001", "10001", "10001" },
            ['I'] = new[] { "01110", "00100", "00100", "00100", "00100", "00100", "01110" },
            ['J'] = new[] { "00111", "00010", "00010", "00010", "00010", "10010", "01100" },
            ['K'] = new[] { "10001", "10010", "10100", "11000", "10100", "10010", "10001" },
            ['L'] = new[] { "10000", "10000", "10000", "10000", "10000", "10000", "11111" },
            ['M'] = new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" },
            ['N'] = new[] { "10001", "10001", "11001", "10101", "10011", "10001", "10001" },
            ['O'] = new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" },
            ['P'] = new[] { "11110", "10001", "10001", "11110", "10000", "10000", "10000" },
            ['Q'] = new[] { "01110", "10001", "10001", "10001", "10101", "10010", "01101" },
            ['R'] = new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" },
            ['S'] = new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" },
            ['T'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" },
            ['U'] = new[] { "10001", "10001", "10001", "10001", "10001", "10001", "01110" },
            ['V'] = new[] { "10001", "10001", "10001", "10001", "10001", "01010", "00100" },
            ['W'] = new[] { "10001", "10001", "10001", "10101", "10101", "10101", "01010" },
            ['X'] = new[] { "10001", "10001", "01010", "00100", "01010", "10001", "10001" },
            ['Y'] = new[] { "10001", "10001", "01010", "00100", "00100", "00100", "00100" },
            ['Z'] = new[] { "11111", "00001", "00010", "00100", "01000", "10000", "11111" },
            ['['] = new[] { "01110", "01000", "01000", "01000", "01000", "01000", "01110" },
            ['\\'] = new[] { "10000", "01000", "01000", "00100", "00010", "00010", "00001" },
            [']'] = new[] { "01110", "00010", "00010", "00010", "00010", "00010", "01110" },
            ['^'] = new[] { "00100", "01010", "10001", "00000", "00000", "00000", "00000" },
            ['_'] = new[] { "00000", "00000", "00000", "00000", "00000", "00000", "11111" },
            ['`'] = new[] { "01000", "00100", "00010", "00000", "00000", "00000", "00000" },
            ['a'] = new[] { "00000", "00000", "01110", "00001", "01111", "10001", "01111" },
            ['b'] = new[] { "10000", "10000", "10110", "11001", "10001", "10001", "11110" },
            ['c'] = new[] { "00000", "00000", "01110", "10000", "10000", "10001", "01110" },
            ['d'] = new[] { "00001", "00001", "01101", "10011", "10001", "10001", "01111" },
            ['e'] = new[] { "00000", "00000", "01110", "10001", "11111", "10000", "01110" },
            ['f'] = new[] { "00110", "01001", "01000", "11100", "01000", "01000", "01000" },
            ['g'] = new[] { "00000", "01111", "10001", "10001", "01111", "00001", "01110" },
            ['h'] = new[] { "10000", "10000", "10110", "11001", "10001", "10001", "10001" },
            ['i'] = new[] { "00100", "00000", "01100", "00100", "00100", "00100", "01110" },
            ['j'] = new[] { "00010", "00000", "00110", "00010", "00010", "10010", "01100" },
            ['k'] = new[] { "10000", "10000", "10010", "10100", "11000", "10100", "10010" },
            ['l'] = new[] { "01100", "00100", "00100", "00100", "00100", "00100", "01110" },
            ['m'] = new[] { "00000", "00000", "11010", "10101", "10101", "10001", "10001" },
            ['n'] = new[] { "00000", "00000", "10110", "11001", "10001", "10001", "10001" },
            ['o'] = new[] { "00000", "00000", "01110", "10001", "10001", "10001", "01110" },
            ['p'] = new[] { "00000", "00000", "11110", "10001", "11110", "10000", "10000" },
            ['q'] = new[] { "00000", "00000", "01101", "10011", "01111", "00001", "00001" },
            ['r'] = new[] { "00000", "00000", "10110", "11001", "10000", "10000", "10000" },
            ['s'] = new[] { "00000", "00000", "01111", "10000", "01110", "00001", "11110" },
            ['t'] = new[] { "01000", "01000", "11100", "01000", "01000", "01001", "00110" },
            ['u'] = new[] { "00000", "00000", "10001", "10001", "10001", "10011", "01101" },
            ['v'] = new[] { "00000", "00000", "10001", "10001", "10001", "01010", "00100" },
            ['w'] = new[] { "00000", "00000", "10001", "10001", "10101", "10101", "01010" },
            ['x'] = new[] { "00000", "00000", "10001", "01010", "00100", "01010", "10001" },
            ['y'] = new[] { "00000", "00000", "10001", "10001", "01111", "00001", "01110" },
            ['z'] = new[] { "00000", "00000", "11111", "00010", "00100", "01000", "11111" },
            ['{'] = new[] { "00110", "01000", "01000", "10000", "01000", "01000", "00110" },
            ['|'] = new[] { "00100", "00100", "00100", "00100", "00100", "00100", "00100" },
            ['}'] = new[] { "01100", "00010", "00010", "00001", "00010", "00010", "01100" },
            ['~'] = new[] { "00000", "01000", "10101", "00010", "00000", "00000", "00000" },
        };

        public static bool TryGetGlyph(char c, out string[] glyph)
        {
            return _glyphs.TryGetValue(c, out glyph);
        }

        public static string[] GetGlyph(char c)
        {
            if (_glyphs.TryGetValue(c, out var glyph))
                return glyph;
            return _glyphs[' '];
        }

        /// <summary>
        /// Renders text onto a raw RGBA byte buffer.
        /// </summary>
        public static void DrawText(byte[] buffer, int bufWidth, int bufHeight, string text,
            int startX, int startY, byte r, byte g, byte b, byte a = 255, int scale = 2)
        {
            int x = startX;
            foreach (char c in text.ToUpper())
            {
                var glyph = GetGlyph(c);
                for (int row = 0; row < CharHeight; row++)
                {
                    for (int col = 0; col < CharWidth; col++)
                    {
                        if (glyph[row][col] == '1')
                        {
                            for (int sy = 0; sy < scale; sy++)
                            {
                                for (int sx = 0; sx < scale; sx++)
                                {
                                    int px = x + col * scale + sx;
                                    int py = startY + row * scale + sy;
                                    if (px >= 0 && px < bufWidth && py >= 0 && py < bufHeight)
                                    {
                                        int idx = (py * bufWidth + px) * 4;
                                        buffer[idx] = r;
                                        buffer[idx + 1] = g;
                                        buffer[idx + 2] = b;
                                        buffer[idx + 3] = a;
                                    }
                                }
                            }
                        }
                    }
                }
                x += (CharWidth + 1) * scale;
            }
        }

        /// <summary>
        /// Measures the rendered width of a text string.
        /// </summary>
        public static int MeasureText(string text, int scale = 2)
        {
            return text.Length * (CharWidth + 1) * scale;
        }
    }

    #endregion

    #region --- RENDERING TYPES ---

    /// <summary>
    /// Manages an OpenGL shader program (vertex + fragment).
    /// </summary>
    public class Shader : IDisposable
    {
        public int Handle { get; private set; }
        private readonly Dictionary<string, int> _uniformCache = new();

        public Shader(string vertexSource, string fragmentSource)
        {
            int vs = CompileShader(ShaderType.VertexShader, vertexSource);
            int fs = CompileShader(ShaderType.FragmentShader, fragmentSource);

            Handle = GL.CreateProgram();
            GL.AttachShader(Handle, vs);
            GL.AttachShader(Handle, fs);
            GL.LinkProgram(Handle);

            GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int status);
            if (status == 0)
            {
                string log = GL.GetProgramInfoLog(Handle);
                throw new Exception($"Shader program link failed:\n{log}");
            }

            GL.DetachShader(Handle, vs);
            GL.DetachShader(Handle, fs);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }

        private int CompileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
            {
                string log = GL.GetShaderInfoLog(shader);
                throw new Exception($"Shader compile failed ({type}):\n{log}");
            }
            return shader;
        }

        public void Use() => GL.UseProgram(Handle);

        public int GetUniformLocation(string name)
        {
            if (!_uniformCache.TryGetValue(name, out int loc))
            {
                loc = GL.GetUniformLocation(Handle, name);
                _uniformCache[name] = loc;
            }
            return loc;
        }

        public void SetMatrix4(string name, Matrix4 matrix)
        {
            GL.UniformMatrix4(GetUniformLocation(name), false, ref matrix);
        }

        public void SetVector3(string name, Vector3 vec)
        {
            GL.Uniform3(GetUniformLocation(name), vec);
        }

        public void SetVector4(string name, Vector4 vec)
        {
            GL.Uniform4(GetUniformLocation(name), vec);
        }

        public void SetFloat(string name, float value)
        {
            GL.Uniform1(GetUniformLocation(name), value);
        }

        public void SetInt(string name, int value)
        {
            GL.Uniform1(GetUniformLocation(name), value);
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                GL.DeleteProgram(Handle);
                Handle = 0;
            }
        }
    }

    /// <summary>
    /// Represents a vertex with position, normal, and texture coordinates.
    /// </summary>
    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;

        public Vertex(Vector3 pos, Vector3 norm, Vector2 uv)
        {
            Position = pos;
            Normal = norm;
            TexCoord = uv;
        }
    }

    /// <summary>
    /// Manages a vertex array object with associated buffers.
    /// </summary>
    public class Mesh : IDisposable
    {
        public int VAO { get; private set; }
        public int VBO { get; private set; }
        public int EBO { get; private set; }
        public int IndexCount { get; private set; }

        public Mesh(Vertex[] vertices, uint[] indices)
        {
            IndexCount = indices.Length;

            VAO = GL.GenVertexArray();
            VBO = GL.GenBuffer();
            EBO = GL.GenBuffer();

            GL.BindVertexArray(VAO);

            GL.BindBuffer(BufferTarget.ArrayBuffer, VBO);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * Vertex.SizeInBytes,
                vertices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, EBO);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint),
                indices, BufferUsageHint.StaticDraw);

            // Position
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
                Vertex.SizeInBytes, 0);
            GL.EnableVertexAttribArray(0);

            // Normal
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false,
                Vertex.SizeInBytes, sizeof(float) * 3);
            GL.EnableVertexAttribArray(1);

            // TexCoord
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false,
                Vertex.SizeInBytes, sizeof(float) * 6);
            GL.EnableVertexAttribArray(2);

            GL.BindVertexArray(0);
        }

        public void Draw()
        {
            GL.BindVertexArray(VAO);
            GL.DrawElements(PrimitiveType.Triangles, IndexCount,
                DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            if (VAO != 0) GL.DeleteVertexArray(VAO);
            if (VBO != 0) GL.DeleteBuffer(VBO);
            if (EBO != 0) GL.DeleteBuffer(EBO);
            VAO = VBO = EBO = 0;
        }

        // Static SizeInBytes for Vertex
        public static class Vertex
        {
            public static readonly int SizeInBytes = sizeof(float) * 8; // 3 + 3 + 2
        }
    }

    /// <summary>
    /// Manages an OpenGL texture with procedural generation support.
    /// </summary>
    public class Texture : IDisposable
    {
        public int Handle { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public Texture(int width, int height, byte[] data,
            TextureWrapMode wrapS = TextureWrapMode.Repeat,
            TextureWrapMode wrapT = TextureWrapMode.Repeat,
            TextureMinFilter minFilter = TextureMinFilter.LinearMipmapLinear,
            TextureMagFilter magFilter = TextureMagFilter.Linear)
        {
            Width = width;
            Height = height;

            Handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, Handle);

            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrapS);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrapT);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);

            GL.GenerateMipmap(TextureTarget.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Bind(int unit = 0)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, Handle);
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                GL.DeleteTexture(Handle);
                Handle = 0;
            }
        }
    }

    #endregion

    #region --- PROCEDURAL TEXTURE GENERATION ---

    /// <summary>
    /// Generates card face textures, table textures, and other procedural assets.
    /// </summary>
    public static class TextureGenerator
    {
        public static byte[] GenerateCardTexture(int width, int height,
            byte bgColorR, byte bgColorG, byte bgColorB,
            string cardLabel, string cardSubLabel,
            bool isActionCard, out Texture tex)
        {
            byte[] data = new byte[width * height * 4];

            // Background gradient
            for (int y = 0; y < height; y++)
            {
                float t = (float)y / height;
                byte r = (byte)(bgColorR * (0.7f + 0.3f * (1 - t)));
                byte g = (byte)(bgColorG * (0.7f + 0.3f * (1 - t)));
                byte b = (byte)(bgColorB * (0.7f + 0.3f * (1 - t)));

                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;

                    // Border (white frame)
                    int borderWidth = 12;
                    bool inBorder = x < borderWidth || x >= width - borderWidth ||
                                    y < borderWidth || y >= height - borderWidth;
                    if (inBorder)
                    {
                        r = 240; g = 240; b = 235;
                    }

                    // Inner border
                    int innerBorder = borderWidth + 4;
                    bool inInnerBorder = (x >= borderWidth && x < innerBorder) ||
                                         (x >= width - innerBorder && x < width - borderWidth) ||
                                         (y >= borderWidth && y < innerBorder) ||
                                         (y >= height - innerBorder && y < height - borderWidth);
                    if (inInnerBorder && !inBorder)
                    {
                        r = (byte)(r * 0.5f);
                        g = (byte)(g * 0.5f);
                        b = (byte)(b * 0.5f);
                    }

                    // Decorative pattern for action cards
                    if (isActionCard && !inBorder && !inInnerBorder)
                    {
                        int px = x - borderWidth;
                        int py = y - borderWidth;
                        if ((px / 8 + py / 8) % 2 == 0)
                        {
                            r = (byte)(r * 0.85f);
                            g = (byte)(g * 0.85f);
                            b = (byte)(b * 0.85f);
                        }
                    }

                    data[idx] = r;
                    data[idx + 1] = g;
                    data[idx + 2] = b;
                    data[idx + 3] = 255;
                }
            }

            // Draw card label (large, centered)
            int labelScale = 6;
            int labelWidth = BitmapFont.MeasureText(cardLabel, labelScale);
            BitmapFont.DrawText(data, width, height, cardLabel,
                (width / 2) - (labelWidth / 2),
                (height / 2) - (BitmapFont.CharHeight * labelScale / 2),
                255, 255, 255, 255, labelScale);

            // Draw sub-label (smaller, below)
            if (!string.IsNullOrEmpty(cardSubLabel))
            {
                int subScale = 3;
                int subWidth = BitmapFont.MeasureText(cardSubLabel, subScale);
                BitmapFont.DrawText(data, width, height, cardSubLabel,
                    (width / 2) - (subWidth / 2),
                    (height / 2) + (BitmapFont.CharHeight * labelScale / 2) + 10,
                    220, 220, 220, 255, subScale);
            }

            // Corner labels (top-left)
            int cornerScale = 2;
            BitmapFont.DrawText(data, width, height, cardLabel,
                20, 20, 255, 255, 255, 255, cornerScale);

            // Corner labels (bottom-right, rotated would need more work - just draw normally)
            int cornerWidth = BitmapFont.MeasureText(cardLabel, cornerScale);
            BitmapFont.DrawText(data, width, height, cardLabel,
                width - cornerWidth - 20, height - 20 - BitmapFont.CharHeight * cornerScale,
                255, 255, 255, 255, cornerScale);

            tex = new Texture(width, height, data);
            return data;
        }

        public static Texture GenerateCardBackTexture(int width, int height)
        {
            byte[] data = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;

                    // Dark purple/blue background
                    byte r = 25, g = 20, b = 55;

                    // Diamond pattern
                    int cx = width / 2;
                    int cy = height / 2;
                    int dx = Math.Abs(x - cx);
                    int dy = Math.Abs(y - cy);
                    int dist = dx + dy;

                    if (dist % 30 < 4)
                    {
                        r = 60; g = 40; b = 120;
                    }
                    if (dist % 60 < 2)
                    {
                        r = 100; g = 60; b = 180;
                    }

                    // Border
                    if (x < 10 || x >= width - 10 || y < 10 || y >= height - 10)
                    {
                        r = 180; g = 140; b = 220;
                    }

                    // Center logo
                    if (Math.Abs(dx) < 40 && Math.Abs(dy) < 60)
                    {
                        if ((dx + dy) % 8 < 2)
                        {
                            r = 200; g = 150; b = 255;
                        }
                    }

                    data[idx] = r;
                    data[idx + 1] = g;
                    data[idx + 2] = b;
                    data[idx + 3] = 255;
                }
            }

            // Draw "DD" logo
            BitmapFont.DrawText(data, width, height, "DD",
                width / 2 - BitmapFont.MeasureText("DD", 3) / 2,
                height / 2 - BitmapFont.CharHeight * 3 / 2,
                200, 150, 255, 255, 3);

            return new Texture(width, height, data);
        }

        public static Texture GenerateTableTexture(int width, int height)
        {
            byte[] data = new byte[width * height * 4];
            Random rng = new Random(42);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;

                    // Dark felt texture
                    float noise = (float)(rng.NextDouble() * 0.15 - 0.075);
                    byte r = (byte)Math.Clamp(35 + noise * 30, 0, 255);
                    byte g = (byte)Math.Clamp(25 + noise * 25, 0, 255);
                    byte b = (byte)Math.Clamp(18 + noise * 20, 0, 255);

                    // Subtle grid pattern
                    if (x % 4 == 0 && y % 4 == 0)
                    {
                        r = (byte)(r * 0.9f);
                        g = (byte)(g * 0.9f);
                        b = (byte)(b * 0.9f);
                    }

                    // Radial darkening at edges
                    float cx = x / (float)width - 0.5f;
                    float cy = y / (float)height - 0.5f;
                    float dist = (float)Math.Sqrt(cx * cx + cy * cy);
                    float darken = Math.Clamp(1.0f - dist * 1.2f, 0.3f, 1.0f);
                    r = (byte)(r * darken);
                    g = (byte)(g * darken);
                    b = (byte)(b * darken);

                    data[idx] = r;
                    data[idx + 1] = g;
                    data[idx + 2] = b;
                    data[idx + 3] = 255;
                }
            }

            return new Texture(width, height, data);
        }

        public static Texture GenerateGlowTexture(int size)
        {
            byte[] data = new byte[size * size * 4];
            int center = size / 2;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int idx = (y * size + x) * 4;
                    float dx = x - center;
                    float dy = y - center;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    float alpha = Math.Clamp(1.0f - dist / center, 0, 1);
                    alpha = alpha * alpha;

                    data[idx] = 255;
                    data[idx + 1] = 255;
                    data[idx + 2] = 255;
                    data[idx + 3] = (byte)(alpha * 255);
                }
            }

            return new Texture(size, size, data);
        }

        public static Texture GenerateCircleTexture(int size, byte r, byte g, byte b)
        {
            byte[] data = new byte[size * size * 4];
            int center = size / 2;
            int radius = size / 2 - 2;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int idx = (y * size + x) * 4;
                    float dx = x - center;
                    float dy = y - center;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    if (dist <= radius)
                    {
                        float edge = 1.0f - Math.Clamp((dist - radius + 3) / 3, 0, 1);
                        data[idx] = r;
                        data[idx + 1] = g;
                        data[idx + 2] = b;
                        data[idx + 3] = (byte)(255 * edge);
                    }
                    else
                    {
                        data[idx + 3] = 0;
                    }
                }
            }

            return new Texture(size, size, data);
        }
    }

    #endregion

    #region --- MESH GENERATION ---

    /// <summary>
    /// Generates common 3D meshes procedurally.
    /// </summary>
    public static class MeshGenerator
    {
        public static Mesh CreateCardMesh(float width = 1.6f, float height = 2.24f, float depth = 0.02f)
        {
            List<Vertex> vertices = new();
            List<uint> indices = new();

            float hw = width / 2;
            float hh = height / 2;
            float hd = depth / 2;

            // Front face
            vertices.Add(new Vertex(new Vector3(-hw, -hh, hd), new Vector3(0, 0, 1), new Vector2(0, 0)));
            vertices.Add(new Vertex(new Vector3(hw, -hh, hd), new Vector3(0, 0, 1), new Vector2(1, 0)));
            vertices.Add(new Vertex(new Vector3(hw, hh, hd), new Vector3(0, 0, 1), new Vector2(1, 1)));
            vertices.Add(new Vertex(new Vector3(-hw, hh, hd), new Vector3(0, 0, 1), new Vector2(0, 1)));

            // Back face
            vertices.Add(new Vertex(new Vector3(hw, -hh, -hd), new Vector3(0, 0, -1), new Vector2(0, 0)));
            vertices.Add(new Vertex(new Vector3(-hw, -hh, -hd), new Vector3(0, 0, -1), new Vector2(1, 0)));
            vertices.Add(new Vertex(new Vector3(-hw, hh, -hd), new Vector3(0, 0, -1), new Vector2(1, 1)));
            vertices.Add(new Vertex(new Vector3(hw, hh, -hd), new Vector3(0, 0, -1), new Vector2(0, 1)));

            // Top edge
            vertices.Add(new Vertex(new Vector3(-hw, hh, hd), new Vector3(0, 1, 0), new Vector2(0, 0)));
            vertices.Add(new Vertex(new Vector3(hw, hh, hd), new Vector3(0, 1, 0), new Vector2(1, 0)));
            vertices.Add(new Vertex(new Vector3(hw, hh, -hd), new Vector3(0, 1, 0), new Vector2(1, 1)));
            vertices.Add(new Vertex(new Vector3(-hw, hh, -hd), new Vector3(0, 1, 0), new Vector2(0, 1)));

            // Bottom edge
            vertices.Add(new Vertex(new Vector3(-hw, -hh, -hd), new Vector3(0, -1, 0), new Vector2(0, 0)));
            vertices.Add(new Vertex(new Vector3(hw, -hh, -hd), new Vector3(0, -1, 0), new Vector2(1, 0)));
            vertices.Add(new Vertex(new Vector3(hw, -hh, hd), new Vector3(0, -1, 0), new Vector2(1, 1)));
            vertices.Add(new Vertex(new Vector3(-hw, -hh, hd), new Vector3(0, -1, 0), new Vector2(0, 1)));

            // Left edge
            vertices.Add(new Vertex(new Vector3(-hw, -hh, -hd), new Vector3(-1, 0, 0), new Vector2(0, 0)));
            vertices.Add(new Vertex(new Vector3(-hw, -hh, hd), new Vector3(-1, 0, 0), new Vector2(1, 0)));
            vertices.Add(new Vertex(new Vector3(-hw, hh, hd), new Vector3(-1, 0, 0), new Vector2(1, 1)));
            vertices.Add(new Vertex(new Vector3(-hw, hh, -hd), new Vector3(-1, 0, 0), new Vector2(0, 1)));

            // Right edge
            vertices.Add(new Vertex(new Vector3(hw, -hh, hd), new Vector3(1, 0, 0), new Vector2(0, 0)));
            vertices.Add(new Vertex(new Vector3(hw, -hh, -hd), new Vector3(1, 0, 0), new Vector2(1, 0)));
            vertices.Add(new Vertex(new Vector3(hw, hh, -hd), new Vector3(1, 0, 0), new Vector2(1, 1)));
            vertices.Add(new Vertex(new Vector3(hw, hh, hd), new Vector3(1, 0, 0), new Vector2(0, 1)));

            // Indices for each face (2 triangles)
            for (uint i = 0; i < 6; i++)
            {
                uint offset = i * 4;
                indices.Add(offset);
                indices.Add(offset + 1);
                indices.Add(offset + 2);
                indices.Add(offset);
                indices.Add(offset + 2);
                indices.Add(offset + 3);
            }

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        public static Mesh CreateBoxMesh(float width, float height, float depth)
        {
            List<Vertex> vertices = new();
            List<uint> indices = new();

            float hw = width / 2;
            float hh = height / 2;
            float hd = depth / 2;

            // 6 faces, 4 verts each
            Vector3[] positions = {
                // Front
                new(-hw, -hh, hd), new(hw, -hh, hd), new(hw, hh, hd), new(-hw, hh, hd),
                // Back
                new(hw, -hh, -hd), new(-hw, -hh, -hd), new(-hw, hh, -hd), new(hw, hh, -hd),
                // Top
                new(-hw, hh, hd), new(hw, hh, hd), new(hw, hh, -hd), new(-hw, hh, -hd),
                // Bottom
                new(-hw, -hh, -hd), new(hw, -hh, -hd), new(hw, -hh, hd), new(-hw, -hh, hd),
                // Left
                new(-hw, -hh, -hd), new(-hw, -hh, hd), new(-hw, hh, hd), new(-hw, hh, -hd),
                // Right
                new(hw, -hh, hd), new(hw, -hh, -hd), new(hw, hh, -hd), new(hw, hh, hd),
            };

            Vector3[] normals = {
                new(0, 0, 1), new(0, 0, -1), new(0, 1, 0),
                new(0, -1, 0), new(-1, 0, 0), new(1, 0, 0),
            };

            Vector2[] uvs = {
                new(0, 0), new(1, 0), new(1, 1), new(0, 1),
            };

            for (int face = 0; face < 6; face++)
            {
                for (int v = 0; v < 4; v++)
                {
                    vertices.Add(new Vertex(
                        positions[face * 4 + v],
                        normals[face],
                        uvs[v]
                    ));
                }
                uint off = (uint)(face * 4);
                indices.Add(off);
                indices.Add(off + 1);
                indices.Add(off + 2);
                indices.Add(off);
                indices.Add(off + 2);
                indices.Add(off + 3);
            }

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        public static Mesh CreatePlaneMesh(float width, float depth, float y = 0)
        {
            float hw = width / 2;
            float hd = depth / 2;

            Vertex[] vertices = {
                new(new Vector3(-hw, y, -hd), new Vector3(0, 1, 0), new Vector2(0, 0)),
                new(new Vector3(hw, y, -hd), new Vector3(0, 1, 0), new Vector2(1, 0)),
                new(new Vector3(hw, y, hd), new Vector3(0, 1, 0), new Vector2(1, 1)),
                new(new Vector3(-hw, y, hd), new Vector3(0, 1, 0), new Vector2(0, 1)),
            };

            uint[] indices = { 0, 1, 2, 0, 2, 3 };

            return new Mesh(vertices, indices);
        }

        public static Mesh CreateCylinderMesh(float radius, float height, int segments = 16)
        {
            List<Vertex> vertices = new();
            List<uint> indices = new();

            float hh = height / 2;

            // Side vertices
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)(i * 2 * Math.PI / segments);
                float x = (float)Math.Cos(angle) * radius;
                float z = (float)Math.Sin(angle) * radius;

                Vector3 normal = new((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
                float u = (float)i / segments;

                vertices.Add(new Vertex(new Vector3(x, -hh, z), normal, new Vector2(u, 0)));
                vertices.Add(new Vertex(new Vector3(x, hh, z), normal, new Vector2(u, 1)));
            }

            for (int i = 0; i < segments; i++)
            {
                uint baseIdx = (uint)(i * 2);
                indices.Add(baseIdx);
                indices.Add(baseIdx + 1);
                indices.Add(baseIdx + 2);
                indices.Add(baseIdx + 1);
                indices.Add(baseIdx + 3);
                indices.Add(baseIdx + 2);
            }

            // Top cap
            int topCenter = vertices.Count;
            vertices.Add(new Vertex(new Vector3(0, hh, 0), new Vector3(0, 1, 0), new Vector2(0.5f, 0.5f)));

            int topStart = vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)(i * 2 * Math.PI / segments);
                float x = (float)Math.Cos(angle) * radius;
                float z = (float)Math.Sin(angle) * radius;
                vertices.Add(new Vertex(new Vector3(x, hh, z), new Vector3(0, 1, 0),
                    new Vector2(0.5f + 0.5f * (float)Math.Cos(angle), 0.5f + 0.5f * (float)Math.Sin(angle))));
            }

            for (int i = 0; i < segments; i++)
            {
                indices.Add((uint)topCenter);
                indices.Add((uint)(topStart + i));
                indices.Add((uint)(topStart + i + 1));
            }

            // Bottom cap
            int botCenter = vertices.Count;
            vertices.Add(new Vertex(new Vector3(0, -hh, 0), new Vector3(0, -1, 0), new Vector2(0.5f, 0.5f)));

            int botStart = vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)(i * 2 * Math.PI / segments);
                float x = (float)Math.Cos(angle) * radius;
                float z = (float)Math.Sin(angle) * radius;
                vertices.Add(new Vertex(new Vector3(x, -hh, z), new Vector3(0, -1, 0),
                    new Vector2(0.5f + 0.5f * (float)Math.Cos(angle), 0.5f + 0.5f * (float)Math.Sin(angle))));
            }

            for (int i = 0; i < segments; i++)
            {
                indices.Add((uint)botCenter);
                indices.Add((uint)(botStart + i + 1));
                indices.Add((uint)(botStart + i));
            }

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        public static Mesh CreateSphereMesh(float radius, int latSegments = 16, int lonSegments = 24)
        {
            List<Vertex> vertices = new();
            List<uint> indices = new();

            for (int lat = 0; lat <= latSegments; lat++)
            {
                float theta = (float)(lat * Math.PI / latSegments);
                float sinTheta = (float)Math.Sin(theta);
                float cosTheta = (float)Math.Cos(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = (float)(lon * 2 * Math.PI / lonSegments);
                    float sinPhi = (float)Math.Sin(phi);
                    float cosPhi = (float)Math.Cos(phi);

                    float x = radius * sinTheta * cosPhi;
                    float y = radius * cosTheta;
                    float z = radius * sinTheta * sinPhi;

                    Vector3 normal = new(x, y, z);
                    normal.Normalize();
                    Vector2 uv = new((float)lon / lonSegments, (float)lat / latSegments);

                    vertices.Add(new Vertex(new Vector3(x, y, z), normal, uv));
                }
            }

            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    uint first = (uint)(lat * (lonSegments + 1) + lon);
                    uint second = (uint)(first + lonSegments + 1);

                    indices.Add(first);
                    indices.Add(second);
                    indices.Add(first + 1);
                    indices.Add(second);
                    indices.Add(second + 1);
                    indices.Add(first + 1);
                }
            }

            return new Mesh(vertices.ToArray(), indices.ToArray());
        }

        public static Mesh CreateQuadMesh(float width, float height)
        {
            float hw = width / 2;
            float hh = height / 2;

            Vertex[] vertices = {
                new(new Vector3(-hw, -hh, 0), new Vector3(0, 0, 1), new Vector2(0, 0)),
                new(new Vector3(hw, -hh, 0), new Vector3(0, 0, 1), new Vector2(1, 0)),
                new(new Vector3(hw, hh, 0), new Vector3(0, 0, 1), new Vector2(1, 1)),
                new(new Vector3(-hw, hh, 0), new Vector3(0, 0, 1), new Vector2(0, 1)),
            };

            uint[] indices = { 0, 1, 2, 0, 2, 3 };
            return new Mesh(vertices, indices);
        }
    }

    #endregion

    #region --- GAME DATA TYPES ---

    public enum CardType
    {
        Number,
        Skip,
        Block,
        Reverse,
        DrawTwo,
        Swap,
        Overload,
        Drain,
        Double,
        Freeze
    }

    public enum CardColor
    {
        Red,
        Blue,
        Green,
        Yellow,
        Purple
    }

    public struct CardData
    {
        public int Id;
        public CardType Type;
        public CardColor Color;
        public int Value;
        public string Label;
        public string SubLabel;
        public bool IsAction;

        public static CardData CreateNumber(int value, CardColor color)
        {
            return new CardData
            {
                Id = Guid.NewGuid().GetHashCode(),
                Type = CardType.Number,
                Color = color,
                Value = value,
                Label = value.ToString(),
                SubLabel = "",
                IsAction = false
            };
        }

        public static CardData CreateAction(CardType type, CardColor color, string label, string subLabel, int value = 0)
        {
            return new CardData
            {
                Id = Guid.NewGuid().GetHashCode(),
                Type = type,
                Color = color,
                Value = value,
                Label = label,
                SubLabel = subLabel,
                IsAction = true
            };
        }

        public Vector4 GetColorVector()
        {
            return Color switch
            {
                CardColor.Red => new Vector4(0.85f, 0.15f, 0.15f, 1.0f),
                CardColor.Blue => new Vector4(0.15f, 0.35f, 0.85f, 1.0f),
                CardColor.Green => new Vector4(0.15f, 0.7f, 0.25f, 1.0f),
                CardColor.Yellow => new Vector4(0.9f, 0.75f, 0.1f, 1.0f),
                CardColor.Purple => new Vector4(0.6f, 0.2f, 0.8f, 1.0f),
                _ => Vector4.One
            };
        }

        public (byte r, byte g, byte b) GetColorBytes()
        {
            return Color switch
            {
                CardColor.Red => (180, 30, 30),
                CardColor.Blue => (30, 70, 180),
                CardColor.Green => (30, 140, 50),
                CardColor.Yellow => (200, 160, 20),
                CardColor.Purple => (130, 40, 170),
                _ => (128, 128, 128)
            };
        }
    }

    public class Deck
    {
        private List<CardData> _cards = new();
        private Random _rng = new();

        public int Count => _cards.Count;

        public void GenerateStandardDeck()
        {
            _cards.Clear();

            // Number cards: 1-10 in 4 colors, 2 of each
            foreach (CardColor color in new[] { CardColor.Red, CardColor.Blue, CardColor.Green, CardColor.Yellow })
            {
                for (int val = 1; val <= 10; val++)
                {
                    _cards.Add(CardData.CreateNumber(val, color));
                    if (val <= 5)
                        _cards.Add(CardData.CreateNumber(val, color));
                }

                // Action cards
                _cards.Add(CardData.CreateAction(CardType.Skip, color, "SKIP", "Block Turn"));
                _cards.Add(CardData.CreateAction(CardType.Block, color, "BLOCK", "Shield"));
                _cards.Add(CardData.CreateAction(CardType.Reverse, color, "REV", "Fast Decay"));
                _cards.Add(CardData.CreateAction(CardType.DrawTwo, color, "+2", "Draw & Pay"));
            }

            // Special cards
            _cards.Add(CardData.CreateAction(CardType.Swap, CardColor.Purple, "SWAP", "Trade Weight", 0));
            _cards.Add(CardData.CreateAction(CardType.Swap, CardColor.Purple, "SWAP", "Trade Weight", 0));
            _cards.Add(CardData.CreateAction(CardType.Overload, CardColor.Purple, "OVL", "+20 Weight", 20));
            _cards.Add(CardData.CreateAction(CardType.Overload, CardColor.Purple, "OVL", "+20 Weight", 20));
            _cards.Add(CardData.CreateAction(CardType.Drain, CardColor.Purple, "DRN", "-15 Self", 15));
            _cards.Add(CardData.CreateAction(CardType.Drain, CardColor.Purple, "DRN", "-15 Self", 15));
            _cards.Add(CardData.CreateAction(CardType.Double, CardColor.Purple, "2X", "Double Next", 0));
            _cards.Add(CardData.CreateAction(CardType.Freeze, CardColor.Purple, "FRZ", "No Decay", 0));

            Shuffle();
        }

        public void Shuffle()
        {
            int n = _cards.Count;
            while (n > 1)
            {
                n--;
                int k = _rng.Next(n + 1);
                (_cards[n], _cards[k]) = (_cards[k], _cards[n]);
            }
        }

        public CardData Draw()
        {
            if (_cards.Count == 0)
                GenerateStandardDeck();

            CardData card = _cards[0];
            _cards.RemoveAt(0);
            return card;
        }

        public List<CardData> DrawMultiple(int count)
        {
            List<CardData> result = new();
            for (int i = 0; i < count; i++)
                result.Add(Draw());
            return result;
        }
    }

    #endregion

    #region --- GAME STATE ---

    public enum GamePhase
    {
        MainMenu,
        Connecting,
        Matchmaking,
        Playing,
        CardSelected,
        CardAnimating,
        OpponentTurn,
        GameOver
    }

    public enum TurnState
    {
        PlayerTurn,
        OpponentTurn,
        Animating,
        Waiting
    }

    public class PlayerState
    {
        public string Name;
        public int Weight;
        public int MaxWeight = 100;
        public List<CardData> Hand = new();
        public bool HasBlock = false;
        public bool IsFrozen = false;
        public float FreezeTimer = 0;
        public bool IsReversed = false;
        public float ReverseTimer = 0;
        public bool HasDouble = false;

        public float WeightRatio => (float)Weight / MaxWeight;
        public bool IsOverloaded => Weight > MaxWeight;

        public void Reset()
        {
            Weight = 0;
            Hand.Clear();
            HasBlock = false;
            IsFrozen = false;
            FreezeTimer = 0;
            IsReversed = false;
            ReverseTimer = 0;
            HasDouble = false;
        }
    }

    public class GameState
    {
        public GamePhase Phase = GamePhase.MainMenu;
        public TurnState Turn = TurnState.Waiting;

        public PlayerState Player = new() { Name = "You" };
        public PlayerState Opponent = new() { Name = "Opponent" };

        public Deck GameDeck = new();

        public float MatchTimer = 60.0f;
        public float TurnTimer = 8.0f;
        public float WeightDecayRate = 1.0f;

        public int SelectedCardIndex = -1;
        public int HoveredCardIndex = -1;

        public float AnimationTimer = 0;
        public float AnimationDuration = 0.6f;
        public CardData AnimatingCard;
        public bool AnimatingToOpponent;

        public string LastAction = "";
        public float LastActionTimer = 0;

        public int Winner = -1; // -1 = none, 0 = player, 1 = opponent, 2 = tie
        public string GameOverReason = "";

        public bool IsMultiplayer = false;
        public string PlayerId = Guid.NewGuid().ToString("N").Substring(0, 8);

        public void StartNewGame(bool multiplayer = false)
        {
            Player.Reset();
            Opponent.Reset();
            GameDeck.GenerateStandardDeck();

            Player.Hand = GameDeck.DrawMultiple(7);
            Opponent.Hand = GameDeck.DrawMultiple(7);

            MatchTimer = 60.0f;
            TurnTimer = 8.0f;
            WeightDecayRate = 1.0f;

            Phase = GamePhase.Playing;
            Turn = TurnState.PlayerTurn;
            SelectedCardIndex = -1;
            HoveredCardIndex = -1;
            AnimationTimer = 0;
            Winner = -1;
            GameOverReason = "";
            LastAction = "";
            IsMultiplayer = multiplayer;

            LastAction = "Match Started!";
            LastActionTimer = 2.0f;
        }

        public void PlayCard(int cardIndex, bool isPlayer)
        {
            PlayerState actor = isPlayer ? Player : Opponent;
            PlayerState target = isPlayer ? Opponent : Player;

            if (cardIndex < 0 || cardIndex >= actor.Hand.Count)
                return;

            CardData card = actor.Hand[cardIndex];
            actor.Hand.RemoveAt(cardIndex);

            AnimatingCard = card;
            AnimatingToOpponent = isPlayer;
            AnimationTimer = 0;
            Turn = TurnState.Animating;

            // Apply effects
            ApplyCardEffect(card, actor, target);

            LastAction = $"{actor.Name} played {card.Label}!";
            LastActionTimer = 2.5f;
        }

        private void ApplyCardEffect(CardData card, PlayerState actor, PlayerState target)
        {
            switch (card.Type)
            {
                case CardType.Number:
                    int weight = card.Value;
                    if (target.HasBlock)
                    {
                        target.HasBlock = false;
                        LastAction = $"{target.Name} blocked the attack!";
                    }
                    else
                    {
                        if (actor.HasDouble)
                        {
                            weight *= 2;
                            actor.HasDouble = false;
                        }
                        target.Weight = Math.Min(target.Weight + weight, 150);
                    }
                    break;

                case CardType.Skip:
                    // Opponent loses next turn
                    LastAction = $"{target.Name}'s turn skipped!";
                    break;

                case CardType.Block:
                    actor.HasBlock = true;
                    break;

                case CardType.Reverse:
                    target.IsReversed = true;
                    target.ReverseTimer = 5.0f;
                    break;

                case CardType.DrawTwo:
                    var drawn = GameDeck.DrawMultiple(2);
                    target.Hand.AddRange(drawn);
                    break;

                case CardType.Swap:
                    int temp = actor.Weight;
                    actor.Weight = target.Weight;
                    target.Weight = temp;
                    break;

                case CardType.Overload:
                    if (target.HasBlock)
                    {
                        target.HasBlock = false;
                    }
                    else
                    {
                        target.Weight = Math.Min(target.Weight + card.Value, 150);
                    }
                    break;

                case CardType.Drain:
                    actor.Weight = Math.Max(0, actor.Weight - card.Value);
                    break;

                case CardType.Double:
                    actor.HasDouble = true;
                    break;

                case CardType.Freeze:
                    target.IsFrozen = true;
                    target.FreezeTimer = 5.0f;
                    break;
            }

            // Check overload
            if (target.IsOverloaded)
            {
                Winner = actor == Player ? 0 : 1;
                GameOverReason = $"{target.Name} overloaded!";
            }
        }

        public void Update(float deltaTime)
        {
            if (Phase != GamePhase.Playing) return;

            // Match timer
            MatchTimer -= deltaTime;
            if (MatchTimer <= 0)
            {
                MatchTimer = 0;
                // Determine winner by weight
                if (Player.Weight < Opponent.Weight)
                {
                    Winner = 0;
                    GameOverReason = "Lower weight wins!";
                }
                else if (Opponent.Weight < Player.Weight)
                {
                    Winner = 1;
                    GameOverReason = "Lower weight wins!";
                }
                else
                {
                    Winner = 2;
                    GameOverReason = "Tie game!";
                }
                Phase = GamePhase.GameOver;
                return;
            }

            // Weight decay
            if (!Player.IsFrozen)
            {
                float decay = Player.IsReversed ? WeightDecayRate * 3 : WeightDecayRate;
                Player.Weight = Math.Max(0, Player.Weight - (int)(decay * deltaTime));
            }
            if (!Opponent.IsFrozen)
            {
                float decay = Opponent.IsReversed ? WeightDecayRate * 3 : WeightDecayRate;
                Opponent.Weight = Math.Max(0, Opponent.Weight - (int)(decay * deltaTime));
            }

            // Status effect timers
            if (Player.IsReversed)
            {
                Player.ReverseTimer -= deltaTime;
                if (Player.ReverseTimer <= 0)
                    Player.IsReversed = false;
            }
            if (Player.IsFrozen)
            {
                Player.FreezeTimer -= deltaTime;
                if (Player.FreezeTimer <= 0)
                    Player.IsFrozen = false;
            }
            if (Opponent.IsReversed)
            {
                Opponent.ReverseTimer -= deltaTime;
                if (Opponent.ReverseTimer <= 0)
                    Opponent.IsReversed = false;
            }
            if (Opponent.IsFrozen)
            {
                Opponent.FreezeTimer -= deltaTime;
                if (Opponent.FreezeTimer <= 0)
                    Opponent.IsFrozen = false;
            }

            // Animation
            if (Turn == TurnState.Animating)
            {
                AnimationTimer += deltaTime;
                if (AnimationTimer >= AnimationDuration)
                {
                    // Draw replacement card
                    if (AnimatingToOpponent && Player.Hand.Count < 7)
                    {
                        Player.Hand.Add(GameDeck.Draw());
                    }
                    else if (!AnimatingToOpponent && Opponent.Hand.Count < 7)
                    {
                        Opponent.Hand.Add(GameDeck.Draw());
                    }

                    // Switch turns
                    if (Winner == -1)
                    {
                        Turn = AnimatingToOpponent ? TurnState.OpponentTurn : TurnState.PlayerTurn;
                        TurnTimer = 8.0f;
                        SelectedCardIndex = -1;
                    }
                    else
                    {
                        Phase = GamePhase.GameOver;
                    }
                }
            }

            // Turn timer
            if (Turn == TurnState.PlayerTurn || Turn == TurnState.OpponentTurn)
            {
                TurnTimer -= deltaTime;
                if (TurnTimer <= 0)
                {
                    // Auto-pass
                    if (Turn == TurnState.PlayerTurn)
                    {
                        Turn = TurnState.OpponentTurn;
                        TurnTimer = 8.0f;
                        LastAction = "Turn timed out!";
                        LastActionTimer = 1.5f;
                    }
                    else
                    {
                        // AI auto-plays
                        OpponentAIPlay();
                    }
                }
            }

            // AI turn
            if (Turn == TurnState.OpponentTurn && Winner == -1)
            {
                OpponentAIThinkTimer -= deltaTime;
                if (OpponentAIThinkTimer <= 0)
                {
                    OpponentAIPlay();
                }
            }

            // Last action display timer
            if (LastActionTimer > 0)
                LastActionTimer -= deltaTime;
        }

        private float OpponentAIThinkTimer = 1.5f;

        private void OpponentAIPlay()
        {
            if (Opponent.Hand.Count == 0) return;

            // Simple AI: pick the best card
            int bestIndex = 0;
            int bestScore = int.MinValue;

            for (int i = 0; i < Opponent.Hand.Count; i++)
            {
                CardData card = Opponent.Hand[i];
                int score = 0;

                if (card.Type == CardType.Number || card.Type == CardType.Overload)
                {
                    score = card.Value;
                    if (Player.Weight + card.Value > Player.MaxWeight)
                        score += 50; // Winning move
                }
                else if (card.Type == CardType.Drain && Opponent.Weight > 30)
                {
                    score = card.Value;
                }
                else if (card.Type == CardType.Swap && Opponent.Weight > Player.Weight + 20)
                {
                    score = 40;
                }
                else if (card.Type == CardType.Block && Opponent.Weight > 50)
                {
                    score = 25;
                }
                else if (card.Type == CardType.Freeze && Player.Weight > 40)
                {
                    score = 20;
                }
                else
                {
                    score = 10; // Default
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            OpponentAIThinkTimer = 1.5f + new Random().Next(0, 10) * 0.1f;
            PlayCard(bestIndex, false);
        }

        public void EndTurn()
        {
            if (Turn == TurnState.PlayerTurn && SelectedCardIndex >= 0)
            {
                PlayCard(SelectedCardIndex, true);
            }
        }
    }

    #endregion

    #region --- CAMERA ---

    public class FirstPersonCamera
    {
        public Vector3 Position = new(0, 1.2f, 0.8f);
        public Vector3 Target = new(0, 0.5f, -2.5f);
        public Vector3 Up = Vector3.UnitY;

        public float Yaw = (float)Math.PI; // Looking forward (-Z)
        public float Pitch = -0.15f;

        public float Fov = 60.0f;
        public float Aspect = 16.0f / 9.0f;
        public float NearPlane = 0.05f;
        public float FarPlane = 100.0f;

        private float _targetYaw;
        private float _targetPitch;
        private float _bobTimer = 0;
        private float _bobIntensity = 0;

        public void Update(float deltaTime, bool mouseLocked, Vector2 mouseDelta, float sensitivity = 0.003f)
        {
            if (mouseLocked)
            {
                _targetYaw += mouseDelta.X * sensitivity;
                _targetPitch -= mouseDelta.Y * sensitivity;
                _targetPitch = Math.Clamp(_targetPitch, -0.5f, 0.3f);
                _targetYaw = Math.Clamp(_targetYaw, (float)Math.PI - 0.6f, (float)Math.PI + 0.6f);
            }

            // Smooth interpolation
            float lerpFactor = Math.Min(deltaTime * 12.0f, 1.0f);
            Yaw += (_targetYaw - Yaw) * lerpFactor;
            Pitch += (_targetPitch - Pitch) * lerpFactor;

            // Head bob
            _bobTimer += deltaTime * 2.0f;
            float bobY = (float)Math.Sin(_bobTimer) * 0.008f * _bobIntensity;
            float bobX = (float)Math.Cos(_bobTimer * 0.5f) * 0.005f * _bobIntensity;

            // Calculate forward direction
            Vector3 forward = new(
                (float)Math.Sin(Yaw) * (float)Math.Cos(Pitch),
                (float)Math.Sin(Pitch),
                -(float)Math.Cos(Yaw) * (float)Math.Cos(Pitch)
            );

            Position = new Vector3(bobX, 1.2f + bobY, 0.8f);
            Target = Position + forward;
        }

        public Matrix4 GetViewMatrix()
        {
            return Matrix4.LookAt(Position, Target, Up);
        }

        public Matrix4 GetProjectionMatrix()
        {
            return Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(Fov),
                Aspect, NearPlane, FarPlane);
        }

        public void SetBob(float intensity)
        {
            _bobIntensity = intensity;
        }
    }

    #endregion

    #region --- PARTICLE SYSTEM ---

    public struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Color;
        public float Life;
        public float MaxLife;
        public float Size;
    }

    public class ParticleSystem
    {
        private List<Particle> _particles = new();
        private Random _rng = new();

        public void Emit(Vector3 position, int count, Vector3 baseColor,
            float speed = 2.0f, float life = 1.0f, float size = 8.0f)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                float elevation = (float)(_rng.NextDouble() * Math.PI - Math.PI / 2);
                float vel = speed * (0.5f + (float)_rng.NextDouble() * 0.5f);

                Particle p = new()
                {
                    Position = position,
                    Velocity = new Vector3(
                        (float)Math.Cos(angle) * (float)Math.Cos(elevation) * vel,
                        (float)Math.Sin(elevation) * vel + 1.0f,
                        (float)Math.Sin(angle) * (float)Math.Cos(elevation) * vel
                    ),
                    Color = baseColor,
                    Life = life * (0.7f + (float)_rng.NextDouble() * 0.3f),
                    MaxLife = life,
                    Size = size * (0.5f + (float)_rng.NextDouble() * 0.5f)
                };
                _particles.Add(p);
            }
        }

        public void EmitStream(Vector3 from, Vector3 to, int count, Vector3 color)
        {
            for (int i = 0; i < count; i++)
            {
                float t = (float)_rng.NextDouble();
                Vector3 pos = Vector3.Lerp(from, to, t) +
                    new Vector3(
                        (float)(_rng.NextDouble() - 0.5) * 0.3f,
                        (float)(_rng.NextDouble() - 0.5) * 0.3f,
                        (float)(_rng.NextDouble() - 0.5) * 0.3f
                    );

                Particle p = new()
                {
                    Position = pos,
                    Velocity = (to - from).Normalized() * 3.0f +
                        new Vector3(
                            (float)(_rng.NextDouble() - 0.5) * 0.5f,
                            (float)(_rng.NextDouble() - 0.5) * 0.5f,
                            (float)(_rng.NextDouble() - 0.5) * 0.5f
                        ),
                    Color = color,
                    Life = 0.5f + (float)_rng.NextDouble() * 0.5f,
                    MaxLife = 1.0f,
                    Size = 6.0f + (float)_rng.NextDouble() * 4.0f
                };
                _particles.Add(p);
            }
        }

        public void Update(float deltaTime)
        {
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                Particle p = _particles[i];
                p.Position += p.Velocity * deltaTime;
                p.Velocity.Y -= 2.0f * deltaTime; // Gravity
                p.Velocity *= 0.96f; // Damping
                p.Life -= deltaTime;
                _particles[i] = p;

                if (p.Life <= 0)
                    _particles.RemoveAt(i);
            }
        }

        public void Render(Shader shader, Matrix4 view, Matrix4 projection)
        {
            if (_particles.Count == 0) return;

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            GL.DepthMask(false);

            shader.Use();
            shader.SetMatrix4("uView", view);
            shader.SetMatrix4("uProjection", projection);
            shader.SetMatrix4("uModel", Matrix4.Identity);

            GL.Enable(EnableCap.ProgramPointSize);

            foreach (var p in _particles)
            {
                float lifeRatio = p.Life / p.MaxLife;
                shader.SetVector4("uColor", new Vector4(p.Color, lifeRatio));

                float[] posData = new float[]
                {
                    p.Position.X, p.Position.Y, p.Position.Z,
                    p.Velocity.X, p.Velocity.Y, p.Velocity.Z,
                    p.Life, p.Size
                };

                // Render as point
                GL.PointSize(p.Size * lifeRatio);
                GL.Begin(PrimitiveType.Points);
                GL.Vertex3(p.Position);
                GL.End();
            }

            GL.Disable(EnableCap.ProgramPointSize);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        public void Clear()
        {
            _particles.Clear();
        }

        public int Count => _particles.Count;
    }

    #endregion

    #region --- NETWORK CLIENT ---

    public class MatchResult
    {
        [JsonPropertyName("playerId")] public string PlayerId { get; set; }
        [JsonPropertyName("playerName")] public string PlayerName { get; set; }
        [JsonPropertyName("opponentName")] public string OpponentName { get; set; }
        [JsonPropertyName("result")] public string Result { get; set; } // "win", "lose", "tie"
        [JsonPropertyName("playerWeight")] public int PlayerWeight { get; set; }
        [JsonPropertyName("opponentWeight")] public int OpponentWeight { get; set; }
        [JsonPropertyName("matchDuration")] public float MatchDuration { get; set; }
        [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
    }

    public class LeaderboardEntry
    {
        [JsonPropertyName("rank")] public int Rank { get; set; }
        [JsonPropertyName("playerName")] public string PlayerName { get; set; }
        [JsonPropertyName("wins")] public int Wins { get; set; }
        [JsonPropertyName("losses")] public int Losses { get; set; }
        [JsonPropertyName("rating")] public int Rating { get; set; }
    }

    public class NetworkClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public string PlayerName { get; set; } = "Player";

        public NetworkClient(string baseUrl = "http://localhost:3000")
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public async Task<bool> CheckConnectionAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/health");
                IsConnected = response.IsSuccessStatusCode;
                return IsConnected;
            }
            catch
            {
                IsConnected = false;
                return false;
            }
        }

        public async Task<bool> ReportMatchResultAsync(MatchResult result)
        {
            try
            {
                var json = JsonSerializer.Serialize(result);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_baseUrl}/api/match/result", content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<LeaderboardEntry>> GetLeaderboardAsync(int limit = 10)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/leaderboard?limit={limit}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<List<LeaderboardEntry>>(json) ?? new List<LeaderboardEntry>();
                }
            }
            catch { }
            return new List<LeaderboardEntry>();
        }

        public async Task<bool> RegisterMatchStartAsync(string playerId, string playerName)
        {
            try
            {
                var payload = new { playerId, playerName, timestamp = DateTime.UtcNow };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_baseUrl}/api/match/start", content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task SendSpectatorDataAsync(string matchId, object gameState)
        {
            try
            {
                var payload = new { matchId, state = gameState, timestamp = DateTime.UtcNow };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{_baseUrl}/api/match/spectate", content);
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }

    #endregion

    #region --- MAIN GAME WINDOW ---

    public class DeckDuelsGame : GameWindow
    {
        // ── Rendering ──
        private Shader _litShader;
        private Shader _hudShader;
        private Shader _particleShader;
        private Shader _postProcessShader;

        private Mesh _cardMesh;
        private Mesh _tableMesh;
        private Mesh _boxMesh;
        private Mesh _planeMesh;
        private Mesh _cylinderMesh;
        private Mesh _sphereMesh;
        private Mesh _quadMesh;

        private Texture _tableTexture;
        private Texture _cardBackTexture;
        private Texture _glowTexture;
        private Dictionary<string, Texture> _cardTextures = new();

        // ── Game State ──
        private GameState _game = new();
        private FirstPersonCamera _camera = new();
        private ParticleSystem _particles = new();
        private NetworkClient _network;

        // ── Input ──
        private bool _mouseLocked = false;
        private Vector2 _lastMousePos;
        private float _screenShake = 0;
        private float _vignetteStrength = 0.8f;
        private float _chromaticAberration = 0.0f;

        // ── UI ──
        private int _windowWidth = 1280;
        private int _windowHeight = 720;
        private float _uiTime = 0;
        private int _menuSelectedItem = 0;
        private string[] _menuItems = { "PLAY (vs AI)", "PLAY (Online)", "LEADERBOARD", "QUIT" };

        // ── Scene Objects ──
        private List<CardInstance> _playerHandVisuals = new();
        private List<CardInstance> _opponentHandVisuals = new();
        private CardInstance _animatingCard;
        private Vector3 _animatingCardStart;
        private Vector3 _animatingCardEnd;
        private Vector3 _animatingCardMid;

        // ── Post-Processing ──
        private int _fbo;
        private int _fboTexture;
        private int _fboDepth;
        private int _quadVAO;
        private int _quadVBO;

        // ── Timing ──
        private float _totalTime = 0;
        private Stopwatch _stopwatch = new();
        private float _frameTime = 0;
        private int _fps = 0;
        private int _frameCount = 0;
        private float _fpsTimer = 0;

        // ── Leaderboard ──
        private List<LeaderboardEntry> _leaderboard = new();
        private bool _leaderboardLoaded = false;

        public DeckDuelsGame(int width, int height, string title)
            : base(GameWindowSettings.Default,
                   new NativeWindowSettings()
                   {
                       Size = new Vector2i(width, height),
                       Title = title,
                       WindowBorder = WindowBorder.Resizable,
                       WindowState = WindowState.Normal,
                       StartVisible = true,
                       API = new GraphicsAPI(ContextAPI.OpenGL, new Version(3, 3), ContextProfile.Core, ContextFlags.Default, ContextReleaseBehavior.Undefined),
                       Flags = ContextFlags.Default,
                   })
        {
            _windowWidth = width;
            _windowHeight = height;
            _network = new NetworkClient();
        }

        #region --- INITIALIZATION ---

        protected override void OnLoad()
        {
            base.OnLoad();

            GL.ClearColor(0.05f, 0.04f, 0.08f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.Multisample);

            // Compile shaders
            _litShader = new Shader(Shaders.LitVertex, Shaders.LitFragment);
            _hudShader = new Shader(Shaders.HUDVertex, Shaders.HUDFragment);
            _particleShader = new Shader(Shaders.ParticleVertex, Shaders.ParticleFragment);
            _postProcessShader = new Shader(Shaders.PostProcessVertex, Shaders.PostProcessFragment);

            // Generate meshes
            _cardMesh = MeshGenerator.CreateCardMesh(1.5f, 2.1f, 0.02f);
            _tableMesh = MeshGenerator.CreatePlaneMesh(6.0f, 4.0f);
            _boxMesh = MeshGenerator.CreateBoxMesh(1.0f, 1.0f, 1.0f);
            _planeMesh = MeshGenerator.CreatePlaneMesh(1.0f, 1.0f);
            _cylinderMesh = MeshGenerator.CreateCylinderMesh(0.3f, 0.6f, 16);
            _sphereMesh = MeshGenerator.CreateSphereMesh(0.2f, 12, 16);
            _quadMesh = MeshGenerator.CreateQuadMesh(2.0f, 2.0f);

            // Generate textures
            _tableTexture = TextureGenerator.GenerateTableTexture(512, 512);
            _cardBackTexture = TextureGenerator.GenerateCardBackTexture(256, 384);
            _glowTexture = TextureGenerator.GenerateGlowTexture(128);

            // Pre-generate card textures for each type
            GenerateAllCardTextures();

            // Initialize post-processing FBO
            InitializePostProcessing();

            // Set camera
            _camera.Aspect = (float)_windowWidth / _windowHeight;

            // Start stopwatch
            _stopwatch.Start();

            // Try connecting to backend
            _ = Task.Run(async () =>
            {
                bool connected = await _network.CheckConnectionAsync();
                if (connected)
                {
                    _leaderboard = await _network.GetLeaderboardAsync();
                    _leaderboardLoaded = true;
                }
            });

            // Center cursor
            _lastMousePos = new Vector2(_windowWidth / 2, _windowHeight / 2);
        }

        private void GenerateAllCardTextures()
        {
            // Number cards 1-10 for each color
            foreach (CardColor color in new[] { CardColor.Red, CardColor.Blue, CardColor.Green, CardColor.Yellow })
            {
                var (r, g, b) = GetColorBytes(color);
                for (int val = 1; val <= 10; val++)
                {
                    string key = $"num_{color}_{val}";
                    TextureGenerator.GenerateCardTexture(256, 384, r, g, b,
                        val.ToString(), "", false, out var tex);
                    _cardTextures[key] = tex;
                }

                // Action cards
                string[] actionLabels = { "SKIP", "BLOCK", "REV", "+2" };
                CardType[] actionTypes = { CardType.Skip, CardType.Block, CardType.Reverse, CardType.DrawTwo };

                for (int i = 0; i < actionLabels.Length; i++)
                {
                    string key = $"act_{color}_{actionTypes[i]}";
                    TextureGenerator.GenerateCardTexture(256, 384, r, g, b,
                        actionLabels[i], GetActionSubLabel(actionTypes[i]), true, out var tex);
                    _cardTextures[key] = tex;
                }
            }

            // Purple special cards
            var (pr, pg, pb) = GetColorBytes(CardColor.Purple);
            var specials = new[] {
                ("SWAP", CardType.Swap, "Trade Weight"),
                ("OVL", CardType.Overload, "+20 Weight"),
                ("DRN", CardType.Drain, "-15 Self"),
                ("2X", CardType.Double, "Double Next"),
                ("FRZ", CardType.Freeze, "No Decay"),
            };

            foreach (var (label, type, sub) in specials)
            {
                string key = $"act_Purple_{type}";
                TextureGenerator.GenerateCardTexture(256, 384, pr, pg, pb,
                    label, sub, true, out var tex);
                _cardTextures[key] = tex;
            }
        }

        private (byte r, byte g, byte b) GetColorBytes(CardColor color)
        {
            return color switch
            {
                CardColor.Red => (160, 30, 30),
                CardColor.Blue => (30, 60, 170),
                CardColor.Green => (30, 130, 45),
                CardColor.Yellow => (190, 150, 20),
                CardColor.Purple => (120, 35, 160),
                _ => (128, 128, 128)
            };
        }

        private string GetActionSubLabel(CardType type)
        {
            return type switch
            {
                CardType.Skip => "Block Turn",
                CardType.Block => "Shield",
                CardType.Reverse => "Fast Decay",
                CardType.DrawTwo => "Draw & Pay",
                _ => ""
            };
        }

        private void InitializePostProcessing()
        {
            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            _fboTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _fboTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                _windowWidth, _windowHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _fboTexture, 0);

            _fboDepth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24,
                _windowWidth, _windowHeight);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _fboDepth);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // Screen quad for post-processing
            float[] quadVertices = {
                -1, -1, 0, 0,
                 1, -1, 1, 0,
                 1,  1, 1, 1,
                -1,  1, 0, 1,
            };

            _quadVAO = GL.GenVertexArray();
            _quadVBO = GL.GenBuffer();
            GL.BindVertexArray(_quadVAO);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVBO);
            GL.BufferData(BufferTarget.ArrayBuffer, quadVertices.Length * sizeof(float),
                quadVertices, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.BindVertexArray(0);
        }

        #endregion

        #region --- UPDATE LOGIC ---

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);

            float dt = (float)e.Time;
            _totalTime += dt;
            _uiTime += dt;

            // FPS counter
            _frameCount++;
            _fpsTimer += dt;
            if (_fpsTimer >= 1.0f)
            {
                _fps = _frameCount;
                _frameCount = 0;
                _fpsTimer = 0;
            }

            // Screen shake decay
            _screenShake *= 0.92f;
            if (_screenShake < 0.01f) _screenShake = 0;

            // Chromatic aberration decay
            _chromaticAberration *= 0.92f;

            // Camera update
            Vector2 mouseDelta = Vector2.Zero;
            if (_mouseLocked)
            {
                var mouse = MouseState;
                Vector2 current = new(mouse.X, mouse.Y);
                mouseDelta = current - _lastMousePos;
                _lastMousePos = current;
            }

            _camera.Update(dt, _mouseLocked, mouseDelta);
            _camera.SetBob(_game.Phase == GamePhase.Playing ? 1.0f : 0.0f);

            // Game state update
            if (_game.Phase == GamePhase.Playing)
            {
                _game.Update(dt);

                // Particles for weight transfer
                if (_game.Turn == TurnState.Animating && _game.AnimationTimer < 0.3f)
                {
                    Vector3 from = _game.AnimatingToOpponent
                        ? new Vector3(0, 0.3f, 0.5f)
                        : new Vector3(0, 0.5f, -2.5f);
                    Vector3 to = _game.AnimatingToOpponent
                        ? new Vector3(0, 0.5f, -2.5f)
                        : new Vector3(0, 0.3f, 0.5f);

                    if (_frameCount % 3 == 0)
                    {
                        var color = _game.AnimatingCard.GetColorVector();
                        _particles.EmitStream(from, to, 3,
                            new Vector3(color.X, color.Y, color.Z));
                    }
                }

                // Critical weight effects
                if (_game.Player.WeightRatio > 0.8f)
                {
                    _chromaticAberration = Math.Max(_chromaticAberration, (_game.Player.WeightRatio - 0.8f) * 5);
                    _vignetteStrength = 1.5f + (_game.Player.WeightRatio - 0.8f) * 2.0f;
                }
                else
                {
                    _vignetteStrength = 0.8f;
                }

                if (_game.Opponent.IsOverloaded || _game.Player.IsOverloaded)
                {
                    _screenShake = 1.0f;
                    _particles.Emit(new Vector3(0, 0.5f, -2.5f), 30,
                        new Vector3(1.0f, 0.3f, 0.1f), 5.0f, 1.5f, 12.0f);
                }

                // Update card hand visuals
                UpdateCardVisuals();
            }

            // Update particles
            _particles.Update(dt);

            // Handle input
            HandleInput(dt);
        }

        private void UpdateCardVisuals()
        {
            // Update player hand positions
            _playerHandVisuals.Clear();
            int handCount = _game.Player.Hand.Count;
            float fanSpread = Math.Min(handCount * 0.25f, 2.0f);
            float cardSpacing = handCount > 1 ? fanSpread / (handCount - 1) : 0;

            for (int i = 0; i < handCount; i++)
            {
                float t = handCount > 1 ? (float)i / (handCount - 1) - 0.5f : 0;
                float x = t * fanSpread;
                float z = Math.Abs(t) * 0.3f + 0.2f;
                float rotY = -t * 0.4f;
                float y = -0.5f + Math.Abs(t) * 0.1f;

                bool isHovered = i == _game.HoveredCardIndex;
                bool isSelected = i == _game.SelectedCardIndex;

                if (isHovered)
                {
                    y += 0.15f;
                    z -= 0.1f;
                }
                if (isSelected)
                {
                    y += 0.3f;
                    z -= 0.2f;
                }

                var cardData = _game.Player.Hand[i];
                _playerHandVisuals.Add(new CardInstance
                {
                    Data = cardData,
                    Position = new Vector3(x, y, z),
                    Rotation = new Vector3(-0.3f, rotY, 0),
                    Scale = 1.0f,
                    IsFaceUp = true,
                    HoverOffset = isHovered ? 0.15f : (isSelected ? 0.3f : 0)
                });
            }

            // Update opponent hand
            _opponentHandVisuals.Clear();
            int oppHandCount = _game.Opponent.Hand.Count;
            float oppFanSpread = Math.Min(oppHandCount * 0.2f, 1.6f);

            for (int i = 0; i < oppHandCount; i++)
            {
                float t = oppHandCount > 1 ? (float)i / (oppHandCount - 1) - 0.5f : 0;
                float x = t * oppFanSpread;
                float rotY = t * 0.3f;

                _opponentHandVisuals.Add(new CardInstance
                {
                    Data = _game.Opponent.Hand[i],
                    Position = new Vector3(x, 0.3f, -3.5f),
                    Rotation = new Vector3(0.3f, rotY, 0),
                    Scale = 0.85f,
                    IsFaceUp = false,
                    HoverOffset = 0
                });
            }

            // Animating card
            if (_game.Turn == TurnState.Animating && _game.AnimatingCard.Id != 0)
            {
                float t = _game.AnimationTimer / _game.AnimationDuration;
                t = Math.Clamp(t, 0, 1);

                Vector3 start = _game.AnimatingToOpponent
                    ? new Vector3(0, 0.2f, 0.3f)
                    : new Vector3(0, 0.4f, -3.0f);
                Vector3 end = _game.AnimatingToOpponent
                    ? new Vector3(0, 0.4f, -3.0f)
                    : new Vector3(0, 0.2f, 0.3f);
                Vector3 mid = new Vector3(0, 1.5f, -1.2f);

                // Bezier curve
                float u = 1 - t;
                Vector3 pos = u * u * start + 2 * u * t * mid + t * t * end;

                _animatingCard = new CardInstance
                {
                    Data = _game.AnimatingCard,
                    Position = pos,
                    Rotation = new Vector3(-t * 0.5f, t * (float)Math.PI, 0),
                    Scale = 1.2f,
                    IsFaceUp = true,
                    HoverOffset = 0
                };
            }
            else
            {
                _animatingCard = null;
            }
        }

        #endregion

        #region --- INPUT HANDLING ---

        private void HandleInput(float dt)
        {
            var keyboard = KeyboardState;
            var mouse = MouseState;

            // Escape to unlock mouse / quit
            if (IsKeyPressed(Keys.Escape))
            {
                if (_mouseLocked)
                {
                    _mouseLocked = false;
                    CursorVisible = true;
                }
                else if (_game.Phase == GamePhase.Playing)
                {
                    _game.Phase = GamePhase.MainMenu;
                }
                else if (_game.Phase == GamePhase.MainMenu)
                {
                    Close();
                }
            }

            if (_game.Phase == GamePhase.MainMenu)
            {
                HandleMenuInput();
            }
            else if (_game.Phase == GamePhase.Playing)
            {
                HandleGameplayInput(dt);
            }
            else if (_game.Phase == GamePhase.GameOver)
            {
                if (IsKeyPressed(Keys.Enter) || IsKeyPressed(Keys.Space))
                {
                    _game.StartNewGame(_game.IsMultiplayer);

                    // Report match result to backend
                    if (_game.Winner != -1)
                    {
                        var result = new MatchResult
                        {
                            PlayerId = _game.PlayerId,
                            PlayerName = _network.PlayerName,
                            OpponentName = "AI",
                            Result = _game.Winner == 0 ? "win" : (_game.Winner == 1 ? "lose" : "tie"),
                            PlayerWeight = _game.Player.Weight,
                            OpponentWeight = _game.Opponent.Weight,
                            MatchDuration = 60.0f - _game.MatchTimer,
                            Timestamp = DateTime.UtcNow
                        };
                        _ = _network.ReportMatchResultAsync(result);
                    }
                }
                if (IsKeyPressed(Keys.M))
                {
                    _game.Phase = GamePhase.MainMenu;
                }
            }

            // Mouse lock toggle
            if (IsMouseButtonPressed(MouseButton.Left) && !_mouseLocked && _game.Phase == GamePhase.Playing)
            {
                _mouseLocked = true;
                CursorVisible = false;
                _lastMousePos = new Vector2(mouse.X, mouse.Y);
            }
        }

        private void HandleMenuInput()
        {
            if (IsKeyPressed(Keys.Up) || IsKeyPressed(Keys.W))
            {
                _menuSelectedItem--;
                if (_menuSelectedItem < 0) _menuSelectedItem = _menuItems.Length - 1;
            }
            if (IsKeyPressed(Keys.Down) || IsKeyPressed(Keys.S))
            {
                _menuSelectedItem++;
                if (_menuSelectedItem >= _menuItems.Length) _menuSelectedItem = 0;
            }

            if (IsKeyPressed(Keys.Enter) || IsKeyPressed(Keys.Space))
            {
                switch (_menuSelectedItem)
                {
                    case 0:
                        _game.StartNewGame(false);
                        _mouseLocked = true;
                        CursorVisible = false;
                        break;
                    case 1:
                        _game.StartNewGame(true);
                        _mouseLocked = true;
                        CursorVisible = false;
                        // Register match with backend
                        _ = _network.RegisterMatchStartAsync(_game.PlayerId, _network.PlayerName);
                        break;
                    case 2:
                        if (!_leaderboardLoaded)
                        {
                            _ = Task.Run(async () =>
                            {
                                _leaderboard = await _network.GetLeaderboardAsync();
                                _leaderboardLoaded = true;
                            });
                        }
                        break;
                    case 3:
                        Close();
                        break;
                }
            }
        }

        private void HandleGameplayInput(float dt)
        {
            var keyboard = KeyboardState;
            var mouse = MouseState;

            // Card selection via mouse (when unlocked)
            if (!_mouseLocked)
            {
                // Calculate which card is hovered
                float normalizedX = (mouse.X / _windowWidth - 0.5f) * 2; // -1 to 1
                int handCount = _game.Player.Hand.Count;
                float fanSpread = Math.Min(handCount * 0.25f, 2.0f);

                int hovered = -1;
                float closestDist = 0.3f;

                for (int i = 0; i < handCount; i++)
                {
                    float t = handCount > 1 ? (float)i / (handCount - 1) - 0.5f : 0;
                    float cardX = t * fanSpread;
                    float dist = Math.Abs(normalizedX - cardX);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        hovered = i;
                    }
                }

                _game.HoveredCardIndex = hovered;

                // Click to select/play card
                if (IsMouseButtonPressed(MouseButton.Left))
                {
                    if (hovered >= 0 && _game.Turn == TurnState.PlayerTurn)
                    {
                        if (_game.SelectedCardIndex == hovered)
                        {
                            // Double click - play the card
                            _game.PlayCard(hovered, true);
                        }
                        else
                        {
                            _game.SelectedCardIndex = hovered;
                        }
                    }
                }
            }

            // Number keys for card selection
            for (int i = 0; i < Math.Min(9, _game.Player.Hand.Count); i++)
            {
                if (IsKeyPressed(Keys.D1 + i))
                {
                    if (_game.SelectedCardIndex == i && _game.Turn == TurnState.PlayerTurn)
                    {
                        _game.PlayCard(i, true);
                    }
                    else
                    {
                        _game.SelectedCardIndex = i;
                    }
                }
            }

            // Enter to play selected card
            if (IsKeyPressed(Keys.Enter) || IsKeyPressed(Keys.Space))
            {
                if (_game.SelectedCardIndex >= 0 && _game.Turn == TurnState.PlayerTurn)
                {
                    _game.PlayCard(_game.SelectedCardIndex, true);
                }
            }

            // Pass turn
            if (IsKeyPressed(Keys.P))
            {
                if (_game.Turn == TurnState.PlayerTurn)
                {
                    _game.Turn = TurnState.OpponentTurn;
                    _game.TurnTimer = 8.0f;
                    _game.LastAction = "You passed!";
                    _game.LastActionTimer = 1.5f;
                }
            }

            // Release mouse lock
            if (IsKeyPressed(Keys.Tab))
            {
                _mouseLocked = false;
                CursorVisible = true;
            }
        }

        #endregion

        #region --- RENDERING ---

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);

            // Render scene to FBO
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            GL.Viewport(0, 0, _windowWidth, _windowHeight);

            // 3D Scene
            RenderScene();

            // 2D HUD
            RenderHUD();

            // Post-processing
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            RenderPostProcess();

            SwapBuffers();
        }

        private void RenderScene()
        {
            Matrix4 view = _camera.GetViewMatrix();
            Matrix4 projection = _camera.GetProjectionMatrix();

            _litShader.Use();
            _litShader.SetVector3("uLightPos", new Vector3(0, 3.0f, -1.0f));
            _litShader.SetVector3("uLightColor", new Vector3(1.0f, 0.95f, 0.8f));
            _litShader.SetVector3("uViewPos", _camera.Position);
            _litShader.SetFloat("uTime", _totalTime);

            // Table
            RenderTable(view, projection);

            // Weight scale (center of table)
            RenderWeightScale(view, projection);

            // Opponent
            RenderOpponent(view, projection);

            // Player hand cards
            foreach (var card in _playerHandVisuals)
            {
                RenderCard(card, view, projection, true);
            }

            // Opponent hand cards
            foreach (var card in _opponentHandVisuals)
            {
                RenderCard(card, view, projection, false);
            }

            // Animating card
            if (_animatingCard != null)
            {
                RenderCard(_animatingCard, view, projection, true);
            }

            // Particles
            _particles.Render(_particleShader, view, projection);
        }

        private void RenderTable(Matrix4 view, Matrix4 projection)
        {
            Matrix4 model = Matrix4.CreateRotationX(-1.5708f) * Matrix4.CreateTranslation(0, -0.5f, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetMatrix4("uView", view);
            _litShader.SetMatrix4("uProjection", projection);
            _litShader.SetVector4("uBaseColor", new Vector4(0.3f, 0.22f, 0.15f, 1.0f));
            _litShader.SetFloat("uUseTexture", 1.0f);
            _litShader.SetFloat("uEmissive", 0.0f);
            _litShader.SetFloat("uRimPower", 0.0f);
            _litShader.SetVector3("uRimColor", Vector3.Zero);

            _tableTexture.Bind(0);
            _litShader.SetInt("uTexture", 0);
            _tableMesh.Draw();

            // Table edge (darker border)
            model = Matrix4.CreateScale(6.5f, 0.3f, 4.5f) * Matrix4.CreateTranslation(0, -0.65f, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(0.15f, 0.1f, 0.06f, 1.0f));
            _litShader.SetFloat("uUseTexture", 0.0f);
            _boxMesh.Draw();

            // Ambient floor
            model = Matrix4.CreateRotationX(-1.5708f) * Matrix4.CreateScale(1.0f) *
                    Matrix4.CreateTranslation(0, -0.7f, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(0.08f, 0.06f, 0.1f, 1.0f));
            _litShader.SetFloat("uUseTexture", 0.0f);
            var floorMesh = MeshGenerator.CreatePlaneMesh(20, 20);
            floorMesh.Draw();
        }

        private void RenderWeightScale(Matrix4 view, Matrix4 projection)
        {
            // Base pillar
            Matrix4 model = Matrix4.CreateTranslation(0, -0.4f, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(0.2f, 0.2f, 0.25f, 1.0f));
            _litShader.SetFloat("uUseTexture", 0.0f);
            _litShader.SetFloat("uEmissive", 0.1f);
            _cylinderMesh.Draw();

            // Balance beam
            float playerWeight = _game.Player.WeightRatio;
            float oppWeight = _game.Opponent.WeightRatio;
            float tilt = (oppWeight - playerWeight) * 0.5f;
            tilt = Math.Clamp(tilt, -0.4f, 0.4f);

            model = Matrix4.CreateRotationZ(tilt) * Matrix4.CreateTranslation(0, 0.1f, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(0.3f, 0.3f, 0.35f, 1.0f));
            _litShader.SetFloat("uEmissive", 0.05f);
            var beamMesh = MeshGenerator.CreateBoxMesh(2.5f, 0.08f, 0.3f);
            beamMesh.Draw();

            // Player weight indicator (left side)
            float playerBarHeight = 0.1f + playerWeight * 1.5f;
            Vector3 playerColor = playerWeight > 0.8f
                ? new Vector3(1.0f, 0.2f, 0.1f)
                : playerWeight > 0.5f
                    ? new Vector3(1.0f, 0.6f, 0.1f)
                    : new Vector3(0.2f, 0.8f, 0.3f);

            model = Matrix4.CreateScale(0.4f, playerBarHeight, 0.4f) *
                    Matrix4.CreateTranslation(-1.0f, -0.4f + playerBarHeight / 2, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(playerColor, 1.0f));
            _litShader.SetFloat("uEmissive", 0.3f + playerWeight * 0.5f);
            _litShader.SetFloat("uRimPower", 0.5f);
            _litShader.SetVector3("uRimColor", playerColor);
            _boxMesh.Draw();

            // Opponent weight indicator (right side)
            float oppBarHeight = 0.1f + oppWeight * 1.5f;
            Vector3 oppColor = oppWeight > 0.8f
                ? new Vector3(1.0f, 0.2f, 0.1f)
                : oppWeight > 0.5f
                    ? new Vector3(1.0f, 0.6f, 0.1f)
                    : new Vector3(0.2f, 0.8f, 0.3f);

            model = Matrix4.CreateScale(0.4f, oppBarHeight, 0.4f) *
                    Matrix4.CreateTranslation(1.0f, -0.4f + oppBarHeight / 2, -1.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(oppColor, 1.0f));
            _litShader.SetFloat("uEmissive", 0.3f + oppWeight * 0.5f);
            _litShader.SetFloat("uRimPower", 0.5f);
            _litShader.SetVector3("uRimColor", oppColor);
            _boxMesh.Draw();

            // Reset shader params
            _litShader.SetFloat("uRimPower", 0.0f);
            _litShader.SetFloat("uEmissive", 0.0f);
        }

        private void RenderOpponent(Matrix4 view, Matrix4 projection)
        {
            float breathe = (float)Math.Sin(_totalTime * 2) * 0.05f;

            // Opponent body (dark figure)
            Matrix4 model = Matrix4.CreateScale(0.6f, 1.2f + breathe, 0.4f) *
                            Matrix4.CreateTranslation(0, 0.3f, -3.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(0.1f, 0.08f, 0.12f, 1.0f));
            _litShader.SetFloat("uUseTexture", 0.0f);
            _litShader.SetFloat("uEmissive", 0.05f);
            _litShader.SetFloat("uRimPower", 0.8f);
            _litShader.SetVector3("uRimColor", new Vector3(0.4f, 0.2f, 0.6f));
            _boxMesh.Draw();

            // Head
            model = Matrix4.CreateScale(0.3f, 0.3f, 0.3f) *
                    Matrix4.CreateTranslation(0, 1.15f + breathe, -3.5f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(0.12f, 0.1f, 0.14f, 1.0f));
            _sphereMesh.Draw();

            // Glowing eyes
            float eyeGlow = _game.Opponent.WeightRatio > 0.7f ? 1.5f : 0.6f;
            Vector3 eyeColor = _game.Opponent.WeightRatio > 0.7f
                ? new Vector3(1.0f, 0.1f, 0.1f)
                : new Vector3(0.3f, 0.5f, 1.0f);

            model = Matrix4.CreateScale(0.04f, 0.04f, 0.04f) *
                    Matrix4.CreateTranslation(-0.08f, 1.18f + breathe, -3.32f);
            _litShader.SetMatrix4("uModel", model);
            _litShader.SetVector4("uBaseColor", new Vector4(eyeColor, 1.0f));
            _litShader.SetFloat("uEmissive", eyeGlow);
            _sphereMesh.Draw();

            model = Matrix4.CreateScale(0.04f, 0.04f, 0.04f) *
                    Matrix4.CreateTranslation(0.08f, 1.18f + breathe, -3.32f);
            _litShader.SetMatrix4("uModel", model);
            _sphereMesh.Draw();

            // Reset
            _litShader.SetFloat("uRimPower", 0.0f);
            _litShader.SetFloat("uEmissive", 0.0f);
        }

        private void RenderCard(CardInstance card, Matrix4 view, Matrix4 projection, bool isPlayer)
        {
            Matrix4 rotX = Matrix4.CreateRotationX(card.Rotation.X);
            Matrix4 rotY = Matrix4.CreateRotationY(card.Rotation.Y);
            Matrix4 rotZ = Matrix4.CreateRotationZ(card.Rotation.Z);
            Matrix4 scale = Matrix4.CreateScale(card.Scale);
            Matrix4 trans = Matrix4.CreateTranslation(card.Position);

            Matrix4 model = scale * rotX * rotY * rotZ * trans;

            _litShader.SetMatrix4("uModel", model);
            _litShader.SetMatrix4("uView", view);
            _litShader.SetMatrix4("uProjection", projection);

            Vector4 cardColor = card.Data.GetColorVector();
            _litShader.SetVector4("uBaseColor", cardColor);
            _litShader.SetFloat("uEmissive", card.HoverOffset > 0.1f ? 0.3f : 0.05f);
            _litShader.SetFloat("uRimPower", card.HoverOffset > 0.1f ? 0.6f : 0.0f);
            _litShader.SetVector3("uRimColor", new Vector3(cardColor.X, cardColor.Y, cardColor.Z));

            if (card.IsFaceUp)
            {
                // Use card-specific texture
                string key = card.Data.IsAction
                    ? $"act_{card.Data.Color}_{card.Data.Type}"
                    : $"num_{card.Data.Color}_{card.Data.Value}";

                if (_cardTextures.TryGetValue(key, out var tex))
                {
                    tex.Bind(0);
                    _litShader.SetFloat("uUseTexture", 1.0f);
                }
                else
                {
                    _litShader.SetFloat("uUseTexture", 0.0f);
                }
            }
            else
            {
                _cardBackTexture.Bind(0);
                _litShader.SetFloat("uUseTexture", 1.0f);
                _litShader.SetVector4("uBaseColor", Vector4.One);
            }

            _litShader.SetInt("uTexture", 0);
            _cardMesh.Draw();

            _litShader.SetFloat("uRimPower", 0.0f);
            _litShader.SetFloat("uEmissive", 0.0f);
        }

        #endregion

        #region --- HUD RENDERING ---

        private void RenderHUD()
        {
            GL.Disable(EnableCap.DepthTest);

            Matrix4 ortho = Matrix4.CreateOrthographicOffCenter(
                0, _windowWidth, _windowHeight, 0, -1, 1);

            _hudShader.Use();
            _hudShader.SetMatrix4("uProjection", ortho);
            _hudShader.SetFloat("uTime", _uiTime);

            if (_game.Phase == GamePhase.MainMenu)
            {
                RenderMainMenu();
            }
            else if (_game.Phase == GamePhase.Playing)
            {
                RenderGameHUD();
            }
            else if (_game.Phase == GamePhase.GameOver)
            {
                RenderGameOver();
            }

            GL.Enable(EnableCap.DepthTest);
        }

        private void RenderMainMenu()
        {
            // Title
            DrawText("DECK DUELS", _windowWidth / 2 - 200, 120, 255, 100, 200, 255, 6);

            // Subtitle
            string subtitle = "3D CARD BATTLE ARENA";
            int subW = BitmapFont.MeasureText(subtitle, 2);
            DrawText(subtitle, _windowWidth / 2 - subW / 2, 200, 150, 150, 200, 255, 2);

            // Menu items
            for (int i = 0; i < _menuItems.Length; i++)
            {
                bool selected = i == _menuSelectedItem;
                byte r = (byte)(selected ? 255 : 180);
                byte g = (byte)(selected ? 200 : 180);
                byte b = (byte)(selected ? 100 : 180);
                int scale = selected ? 4 : 3;
                int y = 320 + i * 60;

                string prefix = selected ? "> " : "  ";
                string text = prefix + _menuItems[i];
                int w = BitmapFont.MeasureText(text, scale);
                DrawText(text, _windowWidth / 2 - w / 2, y, r, g, b, 255, scale);
            }

            // Instructions
            DrawText("UP/DOWN to navigate  |  ENTER to select  |  ESC to quit",
                _windowWidth / 2 - 300, _windowHeight - 50, 120, 120, 140, 255, 2);

            // Version
            DrawText("v1.0.0  |  OpenTK 4.x  |  OpenGL 3.3",
                20, _windowHeight - 30, 80, 80, 100, 200, 2);
        }

        private void RenderGameHUD()
        {
            // Timer (top center)
            int timerSec = (int)Math.Ceiling(_game.MatchTimer);
            string timerStr = timerSec.ToString();
            bool urgent = timerSec <= 10;
            byte tr = (byte)(urgent ? 255 : 200);
            byte tg = (byte)(urgent ? 50 : 200);
            byte tb = (byte)(urgent ? 50 : 255);
            int tw = BitmapFont.MeasureText(timerStr, 8);
            DrawText(timerStr, _windowWidth / 2 - tw / 2, 20, tr, tg, tb, 255, 8);

            DrawText("TIME", _windowWidth / 2 - 30, 90, 150, 150, 170, 255, 2);

            // Player weight (bottom left)
            DrawWeightBar(20, _windowHeight - 120, 200, 30,
                _game.Player.WeightRatio, "YOU", _game.Player.Weight, _game.Player.MaxWeight);

            // Opponent weight (top right)
            DrawWeightBar(_windowWidth - 220, 120, 200, 30,
                _game.Opponent.WeightRatio, "OPP", _game.Opponent.Weight, _game.Opponent.MaxWeight);

            // Turn indicator
            string turnText = _game.Turn == TurnState.PlayerTurn
                ? "YOUR TURN"
                : _game.Turn == TurnState.OpponentTurn
                    ? "OPPONENT TURN"
                    : "PLAYING...";
            int turnColor = _game.Turn == TurnState.PlayerTurn ? 1 : 0;
            byte tcr = (byte)(turnColor == 1 ? 100 : 200);
            byte tcg = (byte)(turnColor == 1 ? 255 : 100);
            byte tcb = (byte)(turnColor == 1 ? 100 : 100);
            int tw2 = BitmapFont.MeasureText(turnText, 3);
            DrawText(turnText, _windowWidth / 2 - tw2 / 2, _windowHeight - 40, tcr, tcg, tcb, 255, 3);

            // Turn timer bar
            float turnRatio = _game.TurnTimer / 8.0f;
            DrawRect(_windowWidth / 2 - 100, _windowHeight - 60, 200, 6,
                50, 50, 60, 255);
            DrawRect(_windowWidth / 2 - 100, _windowHeight - 60, (int)(200 * turnRatio), 6,
                tcr, tcg, tcb, 255);

            // Card count
            DrawText($"Hand: {_game.Player.Hand.Count}", 20, 30, 200, 200, 220, 255, 3);
            DrawText($"Opp: {_game.Opponent.Hand.Count}", _windowWidth - 150, 30, 200, 200, 220, 255, 3);

            // Last action
            if (_game.LastActionTimer > 0 && !string.IsNullOrEmpty(_game.LastAction))
            {
                float alpha = Math.Min(_game.LastActionTimer, 1.0f);
                int law = BitmapFont.MeasureText(_game.LastAction, 3);
                DrawText(_game.LastAction, _windowWidth / 2 - law / 2, 130,
                    255, 220, 100, (byte)(alpha * 255), 3);
            }

            // Status effects
            int sy = _windowHeight - 160;
            if (_game.Player.HasBlock)
            {
                DrawText("[BLOCK]", 20, sy, 100, 200, 255, 255, 2);
                sy -= 25;
            }
            if (_game.Player.IsReversed)
            {
                DrawText("[REV]", 20, sy, 255, 200, 50, 255, 2);
                sy -= 25;
            }
            if (_game.Player.IsFrozen)
            {
                DrawText("[FRZ]", 20, sy, 100, 150, 255, 255, 2);
                sy -= 25;
            }
            if (_game.Player.HasDouble)
            {
                DrawText("[2X]", 20, sy, 200, 100, 255, 255, 2);
            }

            // Selected card info
            if (_game.SelectedCardIndex >= 0 && _game.SelectedCardIndex < _game.Player.Hand.Count)
            {
                var card = _game.Player.Hand[_game.SelectedCardIndex];
                string info = $"{card.Label} - {card.SubLabel}";
                if (string.IsNullOrEmpty(card.SubLabel))
                    info = card.Label;
                int iw = BitmapFont.MeasureText(info, 3);
                DrawText(info, _windowWidth / 2 - iw / 2, _windowHeight - 100, 255, 255, 200, 255, 3);
                DrawText("ENTER to play  |  ESC to deselect",
                    _windowWidth / 2 - 180, _windowHeight - 70, 150, 150, 170, 255, 2);
            }

            // Controls hint
            DrawText("1-9: Select  |  ENTER: Play  |  P: Pass  |  TAB: Free Mouse  |  ESC: Menu",
                20, 20, 80, 80, 100, 150, 2);

            // FPS
            DrawText($"FPS: {_fps}  |  Particles: {_particles.Count}",
                _windowWidth - 200, _windowHeight - 30, 100, 100, 120, 150, 2);
        }

        private void RenderGameOver()
        {
            // Dark overlay
            DrawRect(0, 0, _windowWidth, _windowHeight, 0, 0, 0, 180);

            string title = _game.Winner == 0 ? "VICTORY!" :
                           _game.Winner == 1 ? "DEFEAT" : "TIE";

            byte tr = (byte)(_game.Winner == 0 ? 100 : _game.Winner == 1 ? 255 : 200);
            byte tg = (byte)(_game.Winner == 0 ? 255 : _game.Winner == 1 ? 50 : 200);
            byte tb = (byte)(_game.Winner == 0 ? 100 : _game.Winner == 1 ? 50 : 100);

            int tw = BitmapFont.MeasureText(title, 8);
            DrawText(title, _windowWidth / 2 - tw / 2, 150, tr, tg, tb, 255, 8);

            // Reason
            int rw = BitmapFont.MeasureText(_game.GameOverReason, 3);
            DrawText(_game.GameOverReason, _windowWidth / 2 - rw / 2, 280, 200, 200, 220, 255, 3);

            // Stats
            DrawText($"Your Weight:     {_game.Player.Weight}/100",
                _windowWidth / 2 - 150, 350, 200, 200, 220, 255, 3);
            DrawText($"Opponent Weight: {_game.Opponent.Weight}/100",
                _windowWidth / 2 - 150, 390, 200, 200, 220, 255, 3);

            // Continue
            DrawText("ENTER: Play Again  |  M: Main Menu",
                _windowWidth / 2 - 200, _windowHeight - 100, 180, 180, 200, 255, 3);
        }

        private void DrawWeightBar(int x, int y, int w, int h, float ratio,
            string label, int current, int max)
        {
            // Background
            DrawRect(x - 2, y - 2, w + 4, h + 4, 40, 40, 50, 255);
            DrawRect(x, y, w, h, 20, 20, 25, 255);

            // Fill
            int fillW = (int)(w * Math.Clamp(ratio, 0, 1));
            byte br, bg, bb;
            if (ratio > 0.8f) { br = 255; bg = 50; bb = 50; }
            else if (ratio > 0.5f) { br = 255; bg = 150; bb = 50; }
            else { br = 50; bg = 200; bb = 80; }

            DrawRect(x, y, fillW, h, br, bg, bb, 255);

            // Label
            DrawText(label, x, y - 25, 200, 200, 220, 255, 2);
            DrawText($"{current}/{max}", x + w - 60, y - 25, 200, 200, 220, 255, 2);
        }

        private void DrawRect(int x, int y, int w, int h, byte r, byte g, byte b, byte a)
        {
            // Use the HUD shader to draw a colored quad
            _hudShader.Use();
            _hudShader.SetVector4("uColor", new Vector4(r / 255f, g / 255f, b / 255f, a / 255f));
            _hudShader.SetFloat("uUseTexture", 0.0f);

            float fx = x / (_windowWidth / 2f) - 1;
            float fy = 1 - y / (_windowHeight / 2f);
            float fw = w / (_windowWidth / 2f);
            float fh = h / (_windowHeight / 2f);

            float[] vertices = {
                fx, fy, 0, 0,
                fx + fw, fy, 1, 0,
                fx + fw, fy - fh, 1, 1,
                fx, fy - fh, 0, 1,
            };

            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float),
                vertices, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);

            GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);

            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
        }

        private void DrawText(string text, int x, int y, byte r, byte g, byte b, byte a, int scale)
        {
            // Create a texture for the text and render it
            int textWidth = BitmapFont.MeasureText(text, scale);
            int textHeight = BitmapFont.CharHeight * scale;

            if (textWidth <= 0 || textHeight <= 0) return;

            byte[] data = new byte[textWidth * textHeight * 4];
            BitmapFont.DrawText(data, textWidth, textHeight, text, 0, 0, r, g, b, a, scale);

            // Create temporary texture
            using Texture tex = new Texture(textWidth, textHeight, data,
                TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
                TextureMinFilter.Linear, TextureMagFilter.Linear);

            _hudShader.Use();
            _hudShader.SetVector4("uColor", Vector4.One);
            _hudShader.SetFloat("uUseTexture", 1.0f);

            tex.Bind(0);
            _hudShader.SetInt("uTexture", 0);

            float fx = x / (_windowWidth / 2f) - 1;
            float fy = 1 - y / (_windowHeight / 2f);
            float fw = textWidth / (_windowWidth / 2f);
            float fh = textHeight / (_windowHeight / 2f);

            float[] vertices = {
                fx, fy, 0, 0,
                fx + fw, fy, 1, 0,
                fx + fw, fy - fh, 1, 1,
                fx, fy - fh, 0, 1,
            };

            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float),
                vertices, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);

            GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);

            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(vbo);
        }

        #endregion

        #region --- POST-PROCESSING ---

        private void RenderPostProcess()
        {
            GL.Disable(EnableCap.DepthTest);

            _postProcessShader.Use();
            _postProcessShader.SetFloat("uTime", _totalTime);
            _postProcessShader.SetFloat("uShakeIntensity", _screenShake);
            _postProcessShader.SetFloat("uVignetteStrength", _vignetteStrength);
            _postProcessShader.SetFloat("uChromaticAberration", _chromaticAberration);
            _postProcessShader.SetVector2("uScreenSize", new Vector2(_windowWidth, _windowHeight));

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _fboTexture);
            _postProcessShader.SetInt("uSceneTexture", 0);

            GL.BindVertexArray(_quadVAO);
            GL.DrawArrays(PrimitiveType.TriangleFan, 0, 4);
            GL.BindVertexArray(0);

            GL.Enable(EnableCap.DepthTest);
        }

        #endregion

        #region --- WINDOW EVENTS ---

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);

            _windowWidth = e.Width;
            _windowHeight = e.Height;
            _camera.Aspect = (float)_windowWidth / _windowHeight;

            // Rebuild FBO
            GL.BindTexture(TextureTarget.Texture2D, _fboTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                _windowWidth, _windowHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24,
                _windowWidth, _windowHeight);

            GL.Viewport(0, 0, _windowWidth, _windowHeight);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
        }

        protected override void OnUnload()
        {
            // Cleanup
            _litShader?.Dispose();
            _hudShader?.Dispose();
            _particleShader?.Dispose();
            _postProcessShader?.Dispose();

            _cardMesh?.Dispose();
            _tableMesh?.Dispose();
            _boxMesh?.Dispose();
            _planeMesh?.Dispose();
            _cylinderMesh?.Dispose();
            _sphereMesh?.Dispose();
            _quadMesh?.Dispose();

            _tableTexture?.Dispose();
            _cardBackTexture?.Dispose();
            _glowTexture?.Dispose();

            foreach (var tex in _cardTextures.Values)
                tex?.Dispose();

            _network?.Dispose();

            if (_fbo != 0) GL.DeleteFramebuffer(_fbo);
            if (_fboTexture != 0) GL.DeleteTexture(_fboTexture);
            if (_fboDepth != 0) GL.DeleteRenderbuffer(_fboDepth);
            if (_quadVAO != 0) GL.DeleteVertexArray(_quadVAO);
            if (_quadVBO != 0) GL.DeleteBuffer(_quadVBO);

            base.OnUnload();
        }

        #endregion
    }

    #endregion

    #region --- CARD INSTANCE ---

    public class CardInstance
    {
        public CardData Data;
        public Vector3 Position;
        public Vector3 Rotation;
        public float Scale;
        public bool IsFaceUp;
        public float HoverOffset;
    }

    #endregion

    #region --- ENTRY POINT ---

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Parse command-line arguments
            int width = 1280;
            int height = 720;
            string title = "DeckDuels - 3D Card Battle Arena";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--width" && i + 1 < args.Length)
                    int.TryParse(args[++i], out width);
                else if (args[i] == "--height" && i + 1 < args.Length)
                    int.TryParse(args[++i], out height);
                else if (args[i] == "--fullscreen")
                {
                    width = 1920;
                    height = 1080;
                }
            }

            using var game = new DeckDuelsGame(width, height, title);
            game.Run();
        }
    }

    #endregion
}
