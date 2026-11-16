shader_type canvas_item;

// 启动 Logo 的高光扫过：一条斜向亮带 + 较宽的柔光
uniform float sweep : hint_range(-1.0, 2.0) = -1.0;
uniform float strength : hint_range(0.0, 2.0) = 0.38;
uniform float brightness : hint_range(0.8, 2.0) = 1.28;

// 淡蓝色描边
uniform vec4 outline_color : source_color = vec4(0.66, 0.84, 1.0, 0.85);
uniform float outline_width : hint_range(0.0, 8.0) = 1.2;

void fragment() {
	vec4 c = texture(TEXTURE, UV) * COLOR;

	// 1) 描边：中心透明、但附近有不透明像素时，填成淡蓝色
	if (outline_width > 0.0 && c.a < 0.45) {
		vec2 px = TEXTURE_PIXEL_SIZE * outline_width;
		float m = 0.0;
		m = max(m, texture(TEXTURE, UV + vec2( px.x,  0.0)).a);
		m = max(m, texture(TEXTURE, UV + vec2(-px.x,  0.0)).a);
		m = max(m, texture(TEXTURE, UV + vec2( 0.0,  px.y)).a);
		m = max(m, texture(TEXTURE, UV + vec2( 0.0, -px.y)).a);
		m = max(m, texture(TEXTURE, UV + vec2( px.x,  px.y)).a);
		m = max(m, texture(TEXTURE, UV + vec2(-px.x,  px.y)).a);
		m = max(m, texture(TEXTURE, UV + vec2( px.x, -px.y)).a);
		m = max(m, texture(TEXTURE, UV + vec2(-px.x, -px.y)).a);

		// 描边透明度跟随整体 modulate（淡入淡出时一起变淡）
		float alpha = m * COLOR.a;
		if (alpha > 0.2) {
			c = vec4(outline_color.rgb, outline_color.a * alpha);
		}
	}

	// 2) 整体提亮一点，避免 Logo 在纯黑背景下显得发闷
	c.rgb = c.rgb * brightness + vec3(0.015);

	// 斜向坐标（左上 → 右下）
	float d = UV.x * 0.62 + (1.0 - UV.y) * 0.38;

	// 宽柔光 + 窄亮线
	float soft = smoothstep(0.34, 0.0, abs(d - sweep)) * 0.35;
	float line = smoothstep(0.07, 0.0, abs(d - sweep)) * 0.65;

	c.rgb += (soft + line) * strength * c.a;
	COLOR = c;
}