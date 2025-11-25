from flask import Flask, request, jsonify, render_template
from flask_sock import Sock
import json

app = Flask(__name__)
sock = Sock(app)

# 전역 변수: HMD 위치 + 버튼 카운트
hmd_position = {"x": 0.0, "y": 0.0, "z": 0.0}
button_pressed_count = 0   # 몇 번 눌렸는지

@app.route('/')
def index():
    return render_template('index.html')

@sock.route('/ws')
def websocket(ws):
    global hmd_position, button_pressed_count

    while True:
        data = ws.receive()
        if not data:
            break

        try:
            msg = json.loads(data)

            # Unity → Flask : HMD 위치
            if "hmd_x" in msg:
                hmd_position = {
                    "x": msg["hmd_x"],
                    "y": msg["hmd_y"],
                    "z": msg["hmd_z"],
                }
                print("HMD Pos:", hmd_position)

            # 누른 횟수만큼 Pressed 순차 전송
            if button_pressed_count > 0:
                ws.send("Pressed")
                print("Sent: Pressed")
                button_pressed_count -= 1

        except Exception as e:
            print("WS Error:", e)

@app.route("/get_hmd_position", methods=["GET"])
def get_hmd_position():
    return jsonify(hmd_position)

@app.route("/press_button", methods=["POST"])
def press_button():
    global button_pressed_count
    button_pressed_count += 1
    print(f"Button armed: will send 'Pressed' x{button_pressed_count}")
    return jsonify({"status": "armed", "count": button_pressed_count})

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5000, debug=False)
