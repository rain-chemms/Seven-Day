shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

uniform vec4 color_a : source_color = vec4(0.12, 0.95, 0.48, 1.0);
uniform vec4 color_b : source_color = vec4(0.28, 0.82, 0.92, 1.0);
uniform vec4 color_c : source_color = vec4(0.62, 0.38, 1.0, 1.0);
uniform float alpha : hint_range(0.0, 1.0) = 0.55;
uniform float speed = 1.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
	float v = 0.0;
	float amp = 0.5;
	for (int i = 0; i < 5; i++) {
		v += amp * noise(p);
		p = p * 2.03 + vec2(17.3, 9.1);
		amp *= 0.5;
	}
	return v;
}

void fragment() {
	vec2 uv = UV;
	float t = TIME * speed;

	float warp = fbm(vec2(uv.x * 2.1 + t * 0.035, uv.y * 1.7 - t * 0.025));

	float top = 0.06 + 0.07 * fbm(vec2(uv.x * 2.6, t * 0.04 + 3.1));
	float bottom = 0.72 + 0.13 * fbm(vec2(uv.x * 3.2 + 8.0, t * 0.045));
	float mask = smoothstep(top - 0.05, top + 0.12, uv.y)
		* (1.0 - smoothstep(bottom, bottom + 0.10, uv.y));

	float folds = fbm(vec2(uv.x * 5.2 + warp * 1.5 + t * 0.06, uv.y * 0.8 - t * 0.09));
	float filaments = fbm(vec2(uv.x * 13.0 + warp * 2.4, t * 0.05));
	float body = smoothstep(0.16, 0.62, folds) * (0.65 + 0.35 * filaments);

	float vertical = smoothstep(top, top + 0.20, uv.y)
		* (1.0 - smoothstep(bottom - 0.22, bottom, uv.y));

	vec3 col = mix(color_a.rgb, color_b.rgb, smoothstep(0.10, 0.55, uv.y));
	col = mix(col, color_c.rgb, smoothstep(0.52, 0.90, uv.y) * 0.55);

	float glow = 0.65 + 0.35 * fbm(vec2(uv.x * 9.0 + t * 0.1, t * 0.08));
	float sideFade = smoothstep(0.0, 0.14, uv.x) * (1.0 - smoothstep(0.86, 1.0, uv.x));
	ALBEDO = col * (0.8 + 0.5 * body);
	ALPHA = mask * body * vertical * glow * sideFade * alpha * 1.5;
}
