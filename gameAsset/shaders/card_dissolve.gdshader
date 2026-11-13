shader_type canvas_item;
// 卡牌消融（燃烧）特效：用噪声把卡面一块块「烧」掉，边缘留一圈橙红的火光。
// 用法：把卡牌渲染到 SubViewport，再把这套材质套在显示它的 TextureRect 上，
//       动画里把 dissolve 从 0 推到 1 即可。

uniform float dissolve : hint_range(0.0, 1.0) = 0.0;
uniform vec3 edge_color : source_color = vec3(1.0, 0.55f, 0.16f);
uniform vec3 char_color : source_color = vec3(0.14f, 0.08f, 0.06f);
uniform float edge_width : hint_range(0.005, 0.25) = 0.055;
uniform float noise_scale : hint_range(1.0, 30.0) = 7.0;
uniform float rim_boost : hint_range(0.0, 3.0) = 1.6;

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
	float v = 0.0;
	float amp = 0.5;
	for (int i = 0; i < 4; i++) {
		v += amp * value_noise(p);
		p *= 2.03;
		amp *= 0.5;
	}
	return v;
}

void fragment() {
	// 卡面纹理
	vec4 tex = texture(TEXTURE, UV);

	// 底部先烧：让噪声阈值从下往上抬
	float bias = (1.0 - UV.y) * 0.45;
	float n = fbm(UV * noise_scale) * 0.9 + bias;

	float d = n - dissolve * 1.25;
	if (d < 0.0) {
		discard;
	}

	// 越靠近消融边界越亮（火光），再往里过渡成焦黑
	float edge = smoothstep(0.0, edge_width, d);
	vec3 burn = mix(edge_color * rim_boost, char_color, edge);
	vec3 col = mix(tex.rgb, burn, 1.0 - edge);

	COLOR = vec4(col, tex.a);
}
