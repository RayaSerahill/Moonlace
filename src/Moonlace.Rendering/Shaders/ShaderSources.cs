namespace Moonlace.Rendering.Shaders;

/// <summary>
/// GLSL sources for the single equipment shader. A #version header matching
/// the actual context (core vs ES) is prepended at compile time.
/// </summary>
internal static class ShaderSources
{
    public const string Vertex = """
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aTangent;
        layout(location = 4) in vec4 aColor;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vTangent;
        out vec4 vColor;

        void main()
        {
            vec4 world = uModel * vec4(aPosition, 1.0);
            vWorldPos = world.xyz;
            vNormal = mat3(uModel) * aNormal;
            vUv = aUv;
            vTangent = aTangent;
            vColor = aColor;
            gl_Position = uProjection * uView * world;
        }
        """;

    public const string Fragment = """
        precision highp float;

        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vTangent;
        in vec4 vColor;

        out vec4 fragColor;

        uniform sampler2D uDiffuseTex;
        uniform sampler2D uNormalTex;
        uniform sampler2D uMaskTex;
        uniform sampler2D uIndexTex;
        uniform sampler2D uSpecularTex;

        uniform bool uHasDiffuse;
        uniform bool uHasNormal;
        uniform bool uHasMask;
        uniform bool uHasIndex;
        uniform bool uHasSpecular;

        uniform int uColorTableRows; // 0, 16 or 32
        uniform vec3 uCtDiffuse[32];
        uniform vec3 uCtSpecular[32];
        uniform vec3 uCtEmissive[32];
        uniform float uCtGloss[32];
        uniform float uCtSpecStrength[32];

        uniform vec3 uCameraPos;
        uniform vec3 uLightDir; // normalized, pointing from the light

        // Neutral placeholder tint for character shaders (skin, hair) whose
        // real color comes from character customization data v1 doesn't read.
        uniform vec3 uBaseTint;

        // Alpha-test cutout. Off for skin: Dawntrail skin normal maps carry
        // non-opacity data in alpha and would discard the whole mesh.
        uniform int uAlphaCutout;

        // Off for skin: face/body vertex colors are blend masks, not albedo.
        uniform int uUseVertexColor;

        vec3 srgbToLinear(vec3 c) { return pow(c, vec3(2.2)); }

        void main()
        {
            vec3 n = normalize(vNormal);
            float alpha = 1.0;

            if (uHasNormal)
            {
                vec4 nm = texture(uNormalTex, vUv);
                alpha = nm.a;
                if (uAlphaCutout != 0 && alpha < 0.35)
                    discard;

                vec3 t = vTangent.xyz;
                if (dot(t, t) > 0.01)
                {
                    t = normalize(t - n * dot(t, n));
                    vec3 b = cross(n, t) * (vTangent.w >= 0.0 ? 1.0 : -1.0);
                    vec3 tn;
                    tn.xy = nm.xy * 2.0 - 1.0;
                    tn.z = sqrt(max(0.0, 1.0 - dot(tn.xy, tn.xy)));
                    n = normalize(mat3(t, b, n) * tn);
                }
            }

            // Material colors from the color table, selected by the id texture.
            vec3 ctDiffuse = vec3(1.0);
            vec3 ctSpecular = vec3(0.25);
            vec3 ctEmissive = vec3(0.0);
            float gloss = 32.0;
            float specStrength = 0.35;

            if (uHasIndex && uColorTableRows > 0)
            {
                vec2 id = texture(uIndexTex, vUv).rg;
                int rowA;
                int rowB;
                float blend;
                if (uColorTableRows == 32)
                {
                    // Dawntrail: red selects one of 16 row pairs, green blends within the pair.
                    int pair = int(floor(id.r * 15.0 + 0.5));
                    rowA = pair * 2;
                    rowB = rowA + 1;
                    blend = id.g;
                }
                else
                {
                    float fRow = id.r * 15.0;
                    rowA = int(floor(fRow));
                    rowB = min(rowA + 1, 15);
                    blend = fract(fRow);
                }

                ctDiffuse = mix(uCtDiffuse[rowA], uCtDiffuse[rowB], blend);
                ctSpecular = mix(uCtSpecular[rowA], uCtSpecular[rowB], blend);
                ctEmissive = mix(uCtEmissive[rowA], uCtEmissive[rowB], blend);
                gloss = max(mix(uCtGloss[rowA], uCtGloss[rowB], blend), 2.0);
                specStrength = mix(uCtSpecStrength[rowA], uCtSpecStrength[rowB], blend);
            }

            vec3 baseColor = ctDiffuse * uBaseTint;
            if (uUseVertexColor != 0)
                baseColor *= vColor.rgb;
            if (uHasDiffuse)
            {
                vec4 diff = texture(uDiffuseTex, vUv);
                baseColor *= srgbToLinear(diff.rgb);
                if (!uHasNormal)
                {
                    alpha = diff.a;
                    if (uAlphaCutout != 0 && alpha < 0.35)
                        discard;
                }
            }

            float ao = 1.0;
            float specMask = 1.0;
            if (uHasMask)
            {
                vec3 mask = texture(uMaskTex, vUv).rgb;
                ao = mix(0.35, 1.0, mask.r);
                specMask = mask.g;
            }
            if (uHasSpecular)
            {
                ctSpecular *= srgbToLinear(texture(uSpecularTex, vUv).rgb);
            }

            // Simple hemispheric ambient + one key light + Blinn-Phong specular.
            vec3 viewDir = normalize(uCameraPos - vWorldPos);
            float ndl = max(dot(n, -uLightDir), 0.0);
            vec3 skyTint = vec3(0.34, 0.33, 0.38);
            vec3 groundTint = vec3(0.22, 0.21, 0.24);
            vec3 ambient = mix(groundTint, skyTint, n.y * 0.5 + 0.5);

            vec3 lit = baseColor * (ambient + vec3(1.05, 1.03, 1.0) * ndl) * ao;

            vec3 h = normalize(-uLightDir + viewDir);
            float spec = pow(max(dot(n, h), 0.0), gloss);
            lit += ctSpecular * spec * specStrength * specMask * ndl;

            // Fill light from the opposite side so the dark side stays readable.
            float fill = max(dot(n, normalize(vec3(uLightDir.x, 0.3, uLightDir.z))), 0.0);
            lit += baseColor * fill * 0.18 * ao;

            lit += ctEmissive;

            // Extended Reinhard rolls off highlights (color-table diffuse can
            // exceed 1.0) instead of clipping them to flat white.
            lit = max(lit, vec3(0.0));
            const float whitePoint = 2.4;
            lit = lit * (vec3(1.0) + lit / (whitePoint * whitePoint)) / (vec3(1.0) + lit);

            fragColor = vec4(pow(lit, vec3(1.0 / 2.2)), alpha);
        }
        """;
}
