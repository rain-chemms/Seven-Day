shader_type spatial;
// 描边（反向外壳 / inverted hull）：把顶点沿法线外推，只渲染背面，
// 从模型轮廓外面露出一圈深色，就是 3渲2 的描边。
// 用法：把它设成 toon_cel 材质的 Next Pass（next_pass）。
render_mode unshaded, cull_front, shadows_disabled, depth_draw_opaque;

// 与 Blender 里描边色一致：#3B2E16
uniform vec3 outline_color : source_color = vec3(0.231, 0.180, 0.086);
// 外推宽度（米）。模型 1 单位 = 1 米时，0.015 ~ 0.022 接近 Blender 里的 0.016
uniform float width : hint_range(0.0, 0.15) = 0.018;
// 想让描边随镜头距离保持粗细一致，可把下面设为 true（按视距缩放）
uniform bool screen_uniform = false;
uniform float reference_distance = 12.0;

void vertex() {
	float w = width;
	if (screen_uniform) {
		// 距离越远，外推越多，屏幕上看起来一样粗
		float d = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
		w *= max(d / reference_distance, 0.25);
	}
	VERTEX += normalize(NORMAL) * w;
}

void fragment() {
	ALBEDO = outline_color;
	ALPHA = 1.0;
}
