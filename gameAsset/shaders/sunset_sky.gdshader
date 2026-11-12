shader_type sky;
// 黄昏火烧云：地平线橙红、中段玫瑰色、天顶靛紫，中间漂浮缓慢移动的云带。
// 用法：Sky.SkyMaterial 指向本 shader，Environment 的 BackgroundMode 设成 Sky。

uniform vec3 zenith_color : source_color = vec3(0.16, 0.13, 0.32);
uniform vec3 mid_color : source_color = vec3(0.72, 0.26, 0.34);
uniform vec3 horizon_color : source_color = vec3(1.0, 0.45, 0.14);
uniform vec3 sun_color : source_color = vec3(1.0, 0.78, 0.42);
uniform vec3 sun_dir = vec3(-0.85, -0.05, -0.50);
uniform float cloud_scale : hint_range(0.5, 8.0) = 2.6;
uniform float cloud_speed : hint_range(0.0, 0.2) = 0.012;
uniform float cloud_amount : hint_range(0.0, 1.0) = 0.75;

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
	for (int i = 0; i < 5; i++) {
		v += amp * value_noise(p);
		p *= 2.02;
		amp *= 0.5;
	}
	return v;
}

void sky() {
	vec3 dir = normalize(EYEDIR);

	// 这个战术镜头几乎全程俯视（视野整个在地平线以下），
	// 所以不能按物理地平线来画，否则背景永远是同一个颜色。
	// 这里把「屏幕上下」重新映射成天空的上下：底部深橙 → 顶部靛紫。
	float t = clamp((dir.y + 0.85) / 0.58, 0.0, 1.0);

	// 渐变用很宽的 smoothstep，避免出现明显的色带分界
	vec3 col = mix(horizon_color, mid_color, smoothstep(0.0, 0.65, t));
	col = mix(col, zenith_color, smoothstep(0.35, 1.0, t));

	// 太阳光晕 + 日轮
	vec3 sd = normalize(sun_dir);
	float sun = max(dot(dir, sd), 0.0);
	col += sun_color * pow(sun, 9.0) * 0.28;
	col += sun_color * pow(sun, 140.0) * 1.4;

	// 云：直接用视线方向的 xz 做无接缝投影，再叠一点纵向偏移
	vec2 uv = dir.xz * 2.2 + vec2(0.0, t * 1.4);
	uv += vec2(TIME * cloud_speed, TIME * cloud_speed * 0.3);

	float n = fbm(uv * 1.1);
	float detail = fbm(uv * 3.6 - TIME * 0.006);
	// 很宽的过渡区间 + 低云量：云是「雾蒙蒙一片」而不是硬边色块
	float cloud = smoothstep(0.38, 0.78, n + 0.25 * detail);

	// 上下两端淡一点，中间最浓
	cloud *= smoothstep(0.0, 0.3, t) * (1.0 - smoothstep(0.7, 1.0, t) * 0.5);

	// 云底被夕阳染橙红，越高越偏紫
	vec3 cloud_lit = mix(vec3(1.0, 0.62, 0.30), vec3(0.85, 0.45, 0.55), smoothstep(0.1, 0.9, t));
	col = mix(col, cloud_lit, cloud * cloud_amount);

	// 底部略压暗，让浮岛更突出
	col *= mix(0.88, 1.0, t);

	// 稍微去饱和，整片天空更统一、不刺眼
	float lum = dot(col, vec3(0.299, 0.587, 0.114));
	col = mix(col, vec3(lum), 0.16);

	// 加一点点抖动，消除大面积渐变的色带
	col += (hash21(dir.xz * 512.0) - 0.5) * 0.012;

	COLOR = col;
}
