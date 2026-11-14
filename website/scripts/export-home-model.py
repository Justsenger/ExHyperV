"""Run with Blender: --background --disable-autoexec SOURCE.blend --python this.py -- OUTPUT.glb.

Keep the eight source meshes and camera. Bake the original procedural emission
into self-contained textures so the website preserves the icon's gradients.
"""
import bpy
import math
from pathlib import Path
import sys

destination = Path(sys.argv[sys.argv.index('--') + 1]).resolve()
source = bpy.context.scene
cells = sorted((obj for obj in source.objects if obj.type == 'MESH' and obj.name.startswith('Module_')), key=lambda obj: obj.name)
if len(cells) != 8:
    raise RuntimeError(f'Expected eight source cells, found {len(cells)}')
bpy.context.view_layer.update()
exposure = 2 ** source.view_settings.exposure
web = bpy.data.scenes.new('ExHyperV icon')
web.render.engine = 'CYCLES'
web.cycles.device = 'CPU'
web.cycles.samples = 1
web.render.bake.margin = 8
web.render.resolution_x = 640
web.render.resolution_y = 640
web.view_settings.view_transform = 'Standard'
web.view_settings.look = 'None'
web.view_settings.exposure = 0

camera = source.camera.copy()
camera.data = source.camera.data.copy()
camera.parent = None
camera.matrix_world = source.camera.matrix_world.copy()
camera.name = 'Icon camera'
camera.animation_data_clear()
web.collection.objects.link(camera)
web.camera = camera

copies = []
for number, original in enumerate(cells, 1):
    mesh = original.copy()
    mesh.data = original.data.copy()
    mesh.parent = None
    mesh.matrix_world = original.matrix_world.copy()
    mesh.animation_data_clear()
    mesh.name = f'Crystal {number:02d}'
    mesh.data.materials.clear()
    material = original.data.materials[0].copy()
    material.animation_data_clear()
    material.name = f'Gradient {number:02d}'
    for node in material.node_tree.nodes:
        if node.type == 'EMISSION':
            node.inputs['Strength'].default_value *= exposure
    mesh.data.materials.append(material)
    web.collection.objects.link(mesh)
    copies.append(mesh)

bpy.context.window.scene = web
for number, mesh in enumerate(copies, 1):
    bpy.ops.object.select_all(action='DESELECT')
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    while mesh.data.uv_layers:
        mesh.data.uv_layers.remove(mesh.data.uv_layers[0])
    mesh.data.uv_layers.new(name='Gradient UV')
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.025)
    bpy.ops.object.mode_set(mode='OBJECT')
    image = bpy.data.images.new(f'Gradient {number:02d}', width=256, height=256, alpha=False)
    image.colorspace_settings.name = 'sRGB'
    material = mesh.data.materials[0]
    texture = material.node_tree.nodes.new('ShaderNodeTexImage')
    texture.image = image
    material.node_tree.nodes.active = texture
    bpy.ops.object.bake(type='EMIT', use_clear=True, margin=8)
    image.pack()
    material.node_tree.nodes.clear()
    texture = material.node_tree.nodes.new('ShaderNodeTexImage')
    texture.image = image
    emission = material.node_tree.nodes.new('ShaderNodeEmission')
    emission.inputs['Strength'].default_value = 1
    output = material.node_tree.nodes.new('ShaderNodeOutputMaterial')
    material.node_tree.links.new(texture.outputs['Color'], emission.inputs['Color'])
    material.node_tree.links.new(emission.outputs[0], output.inputs['Surface'])
    print(f'Baked cell {number}/8', flush=True)

destination.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.export_scene.gltf(filepath=str(destination), export_format='GLB',
    use_active_scene=True, export_cameras=True, export_lights=False,
    export_animations=False, export_extras=False, export_apply=True)
print(f'Exported eight cells: {destination.name} ({destination.stat().st_size} bytes)', flush=True)
