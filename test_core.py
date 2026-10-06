import ctypes
import json
import pathlib
import tempfile

dll = ctypes.CDLL(str(pathlib.Path(__file__).parent / 'app' / 'RemoteRooms.Core.dll'))
dll.rr_open.argtypes = [ctypes.c_wchar_p]
dll.rr_open.restype = ctypes.c_int
dll.rr_state.restype = ctypes.c_char_p
dll.rr_apply.argtypes = [ctypes.c_char_p]
dll.rr_apply.restype = ctypes.c_int
dll.rr_undo.restype = ctypes.c_int
dll.rr_redo.restype = ctypes.c_int

def state():
    return json.loads(dll.rr_state())

def apply(command):
    assert dll.rr_apply(json.dumps(command, ensure_ascii=False).encode('utf-8')) == 1

with tempfile.TemporaryDirectory() as directory:
    path = str(pathlib.Path(directory) / 'address-book.json')
    assert dll.rr_open(path) == 1
    apply({'op': 'addRoom', 'name': 'Кімната 1'})
    room = state()['rooms'][0]
    apply({'op': 'addDesk', 'roomId': room['id'], 'x': 75, 'y': 80})
    apply({'op': 'addComputer', 'roomId': room['id'], 'name': 'ПК 1', 'x': 100, 'y': 180})
    pc = state()['computers'][0]
    apply({'op': 'addEndpoint', 'computerId': pc['id'], 'protocol': 'SSH', 'host': 'example.local', 'port': 22, 'username': 'admin'})
    assert len(state()['endpoints']) == 1
    assert dll.rr_undo() == 1 and not state()['endpoints']
    assert dll.rr_redo() == 1 and len(state()['endpoints']) == 1
    assert dll.rr_open(path) == 1 and len(state()['endpoints']) == 1
    assert 'password' not in pathlib.Path(path).read_text()
    desk_id = state()['desks'][0]['id']
    apply({'op': 'delete', 'collection': 'desks', 'id': desk_id})
    assert not state()['desks']
    assert dll.rr_undo() == 1 and len(state()['desks']) == 1
    apply({'op': 'delete', 'collection': 'computers', 'id': pc['id']})
    assert not state()['computers'] and not state()['endpoints']
    assert dll.rr_open(path) == 1 and len(state()['desks']) == 1
    assert not state()['computers'] and not state()['endpoints']
print('Native model: OK')
