shader_type spatial;
// 3渲2 基础材质：平涂贴图 + 三段色阶（对应 Blender 里的 ColorRamp 0.42 / 0.72）
// 用法：新建 ShaderMaterial → Shader 选本文件 → albedo_tex 指定模型用的图集 PNG
render_mode cull_back;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec3 albedo_tint : source_color = vec3(1.0, 1.0, 1.0);
// 色阶分界，和 Blender 里一致：0.42 / 0.72
uniform float band_low : hint_range(0.0, 1.0) = 0.42;
uniform float band_high : hint_range(0.0, 1.0) = 0.72;
// 三段的亮度系数（暗部 / 中间调 / 亮部）
uniform float level_dark : hint_range(0.0, 1.5) = 0.62;
uniform float level_mid : hint_range(0.0, 1.5) = 0.82;
uniform float level_lit : hint_range(0.0, 1.5) = 1.0;
// 环境光补偿：Godot 的 ambient 在自定义 light() 里不会自动叠加，用它兜底
uniform float ambient_boost : hint_range(0.0, 1.0) = 0.18;

void fragment() {
	ALBEDO = texture(albedo_tex, UV).rgb * albedo_tint;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
}

void light() {
	float ndl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
	float level = level_dark;
	level = mix(level, level_mid, step(band_low, ndl));
	level = mix(level, level_lit, step(band_high, ndl));
	// 硬边阶跃：完全不用 smoothstep，保留卡通感
	DIFFUSE_LIGHT += (level + ambient_boost) * ATTENUATION * LIGHT_COLOR / PI;
}
