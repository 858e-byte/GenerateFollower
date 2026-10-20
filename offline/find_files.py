"""Existence de fichiers relatifs à Data (loose ou BSA). Lecture seule.

Usage : find_files.py <liste.json>  ->  JSON {"textures\\…": true|false}
"""
import sys, json
import assets

print(json.dumps({rel: bool(assets.find(rel)) for rel in json.load(open(sys.argv[1], encoding='utf-8'))}))
