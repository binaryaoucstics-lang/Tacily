"""Offline JSONL fixture, intentionally live=false. Never supplies real evidence."""
import json
import sys

seen = set()
for line in sys.stdin:
    request = json.loads(line)
    identity = {"id": request.get("id"), "caseId": request.get("caseId")}
    if not all(isinstance(value, str) and value.strip() for value in identity.values()):
        events = [{**identity, "type": "error", "code": "invalid_identity"}]
    elif request["action"] != "cancel" and request["id"] in seen:
        events = [{**identity, "type": "error", "code": "duplicate_id"}]
    elif request["action"] == "capabilities":
        events = [{**identity, "type": "capabilities", "live": False, "provider": "synthetic",
                   "targetLanguage": "zh-CN", "policyVersion": 1}]
        if "--wrong-capabilities-id" in sys.argv:
            events[0]["id"] = "wrong-request"
        if "--wrong-capabilities-case" in sys.argv:
            events[0]["caseId"] = "wrong-case"
    elif request["action"] == "assess":
        events = [{**identity, "type": "assessment", "needsTranslation": False}]
    elif request["action"] == "message":
        message = request["message"]
        packet = {key: message[key] for key in ("requestId", "documentToken", "generation", "revision")}
        events = [{**identity, "type": "packet", "packet": json.dumps({**packet, "type": "pageAutoAssessment", "needsTranslation": True})},
                  {**identity, "type": "packet", "packet": json.dumps({**packet, "type": "pageAutoTranslations", "finished": True,
                    "updates": [{"id": block["id"], "text": "Synthetic test output", "complete": True} for block in message["blocks"]]})},
                  {**identity, "type": "terminal", "success": True, "requestCount": 0, "failedCount": 0, "cacheHits": 0}]
    else:
        events = [{**identity, "type": "terminal", "success": True, "requestCount": 0, "failedCount": 0, "cacheHits": 0}]
    seen.add(request.get("id"))
    for event in events:
        print(json.dumps(event), flush=True)
