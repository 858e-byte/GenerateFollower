"""Génère hors jeu la tête FaceGen d'un preset : <sortie>\\<nom>.nif + <nom>.dds (teinte).

Usage : make_head.py <preset> <race> <fiche headparts.json> <dossier sortie> <nom> [chemin teinte dans le nif] [taille]
Exemple de chemin teinte pour le build : Actors\\Character\\FaceGenData\\FaceTint\\GenerateFollower.esp\\00000800.dds
"""
import sys, os, json, time, shutil
import assets, assemble, tint


def make_head(preset, race, hp_json, out_dir, name, tint_ref=None, size=1024):
    t0 = time.time()
    os.makedirs(out_dir, exist_ok=True)
    nif = os.path.join(out_dir, name + '.nif')
    dds = os.path.join(out_dir, name + '.dds')
    report = assemble.assemble(preset, race, hp_json, nif, tint_ref or dds)
    # Teinte : celle exportée avec la tête RaceMenu importée (maquillage compris), sauf si elle est uniforme
    # (les exports UBE sont gris uni) ; sinon composition des calques du jslot
    imp = json.load(open(hp_json, encoding='utf-8-sig')).get('import')
    imp_dds = os.path.splitext(imp)[0] + '.dds' if imp else None
    if imp_dds and os.path.isfile(imp_dds) and tint.load_dds(imp_dds, 64)[..., :3].std() >= 0.01:
        shutil.copyfile(imp_dds, dds)
        return report + [f'teinte importée de {os.path.basename(imp_dds)}', f'{time.time() - t0:.1f} s']
    j = json.load(open(assets.DATA + rf"\SKSE\Plugins\CharGen\Presets\{preset}.jslot", encoding='utf-8'))
    tint.save_dds(tint.compose(j, size, 'r'), dds)
    return report + [f'teinte {size}² -> {dds}', f'{time.time() - t0:.1f} s']


if __name__ == '__main__':
    a = sys.argv[1:]
    for line in make_head(a[0], a[1], a[2], a[3], a[4], a[5] if len(a) > 5 else None, int(a[6]) if len(a) > 6 else 1024):
        print(line)
