extends SceneTree

func _initialize() -> void:
    var base := "res://gameAsset/models/decorates"
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(base + "/nuclearBomb"))
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(base + "/mushroomCloud"))
    _export_bomb(base + "/nuclearBomb/nuclear_bomb.glb")
    _export_mushroom(base + "/mushroomCloud/mushroom_cloud.glb")
    quit()

func _mat(c: Color, unshaded := false) -> StandardMaterial3D:
    var m := StandardMaterial3D.new()
    m.albedo_color = c
    if unshaded:
        m.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
    return m

func _mesh(parent: Node3D, mesh: Mesh, pos: Vector3, rot: Vector3, material: StandardMaterial3D) -> MeshInstance3D:
    var mi := MeshInstance3D.new()
    mi.mesh = mesh
    mi.position = pos
    mi.rotation = rot
    mi.material_override = material
    mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
    parent.add_child(mi)
    return mi

func _export_bomb(path: String) -> void:
    var root := Node3D.new()
    root.name = "NuclearBomb"

    var body_mat := _mat(Color("4b5053"))
    var dark_mat := _mat(Color("23272a"))
    var yellow := _mat(Color("e2b23e"), true)
    var black := _mat(Color("17191a"), true)

    # 透明棕色外壳：包住弹体，可看到内部金属弹体与标识。
    var shell_mat := _mat(Color(0.43, 0.23, 0.10, 0.38), true)
    shell_mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
    shell_mat.cull_mode = BaseMaterial3D.CULL_DISABLED
    shell_mat.no_depth_test = false
    var shell := CapsuleMesh.new()
    shell.radius = 0.43
    shell.height = 2.05
    shell.radial_segments = 14
    shell.rings = 6
    _mesh(root, shell, Vector3.ZERO, Vector3.ZERO, shell_mat)

    # 弹体：头部朝下，尾部朝上。
    var body := CapsuleMesh.new()
    body.radius = 0.32
    body.height = 1.55
    body.radial_segments = 12
    body.rings = 5
    _mesh(root, body, Vector3.ZERO, Vector3.ZERO, body_mat)

    var nose := CylinderMesh.new()
    nose.top_radius = 0.32
    nose.bottom_radius = 0.015
    nose.height = 0.65
    nose.radial_segments = 12
    _mesh(root, nose, Vector3(0, -1.08, 0), Vector3.ZERO, dark_mat)

    var band := CylinderMesh.new()
    band.top_radius = 0.335
    band.bottom_radius = 0.335
    band.height = 0.1
    band.radial_segments = 12
    _mesh(root, band, Vector3(0, -0.35, 0), Vector3.ZERO, yellow)

    # 尾翼。
    for i in range(4):
        var fin := BoxMesh.new()
        fin.size = Vector3(0.78, 0.42, 0.055)
        var a := i * PI * 0.5
        _mesh(root, fin, Vector3(cos(a) * 0.31, 0.78, sin(a) * 0.31), Vector3(0, -a, 0), dark_mat)

    # 两侧放射性标识：黄色圆盘 + 三个黑色扇叶 + 中心黑点。
    for side in [-1.0, 1.0]:
        var disc := CylinderMesh.new()
        disc.top_radius = 0.30
        disc.bottom_radius = 0.30
        disc.height = 0.018
        disc.radial_segments = 20
        _mesh(root, disc, Vector3(side * 0.345, 0.05, 0), Vector3(0, 0, PI * 0.5), yellow)

        var hub := CylinderMesh.new()
        hub.top_radius = 0.045
        hub.bottom_radius = 0.045
        hub.height = 0.025
        hub.radial_segments = 10
        _mesh(root, hub, Vector3(side * 0.35, 0.05, 0), Vector3(0, 0, PI * 0.5), black)

        for k in range(3):
            var blade := BoxMesh.new()
            blade.size = Vector3(0.022, 0.085, 0.26)
            var ang := k * TAU / 3.0
            var py := 0.05 + cos(ang) * 0.145
            var pz := sin(ang) * 0.145
            _mesh(root, blade, Vector3(side * 0.365, py, pz), Vector3(ang, 0, 0), black)

    # 尾部小灯，方便在暗背景中看到下落中的弹体。
    var tail := CylinderMesh.new()
    tail.top_radius = 0.2
    tail.bottom_radius = 0.2
    tail.height = 0.12
    tail.radial_segments = 12
    _mesh(root, tail, Vector3(0, 1.0, 0), Vector3.ZERO, _mat(Color("ff9a3c"), true))

    var doc := GLTFDocument.new()
    var state := GLTFState.new()
    var err := doc.append_from_scene(root, state)
    if err == OK:
        err = doc.write_to_filesystem(state, path)
    print("[NuclearModels] bomb err=", err, " path=", path)

func _export_mushroom(path: String) -> void:
    var root := Node3D.new()
    root.name = "MushroomCloud"

    var stem_mat := _mat(Color("a9633c"))
    var cap_mat := _mat(Color("5f5b55"))
    var dark_mat := _mat(Color("403d39"))
    var ember := _mat(Color("d48745"), true)

    var stem := CylinderMesh.new()
    stem.top_radius = 2.35
    stem.bottom_radius = 0.72
    stem.height = 4.6
    stem.radial_segments = 14
    _mesh(root, stem, Vector3(0, 2.3, 0), Vector3.ZERO, stem_mat)

    var cap := SphereMesh.new()
    cap.radius = 3.55
    cap.height = 2.1
    cap.radial_segments = 18
    cap.rings = 9
    _mesh(root, cap, Vector3(0, 5.15, 0), Vector3.ZERO, cap_mat).scale = Vector3(1.15, 0.54, 1.15)

    # 云盖周围的烟团。
    for i in range(8):
        var a := TAU * i / 8.0
        var puff := SphereMesh.new()
        puff.radius = 1.05
        puff.height = 0.85
        puff.radial_segments = 10
        puff.rings = 5
        _mesh(root, puff,
            Vector3(cos(a) * 2.6, 4.8 + (i % 3) * 0.35, sin(a) * 2.6),
            Vector3.ZERO, dark_mat if i % 2 == 0 else cap_mat)

    # 底部橙色火环。
    var ring := TorusMesh.new()
    ring.inner_radius = 2.8
    ring.outer_radius = 3.35
    ring.rings = 10
    ring.ring_segments = 24
    _mesh(root, ring, Vector3(0, 0.12, 0), Vector3.ZERO, ember)

    var doc := GLTFDocument.new()
    var state := GLTFState.new()
    var err := doc.append_from_scene(root, state)
    if err == OK:
        err = doc.write_to_filesystem(state, path)
    print("[NuclearModels] mushroom err=", err, " path=", path)
