option casemap:none

PUBLIC FollowCameraCapture

extern followCameraReturnAddress : QWORD
extern capturedFollowCamera      : QWORD

.code
FollowCameraCapture PROC
    ; Capture the live follow-camera object without changing registers/flags.
    mov qword ptr [capturedFollowCamera], rbx

    ; Replay the two complete instructions replaced by the absolute jump.
    movss xmm2, dword ptr [rbx+160h]
    movss xmm3, dword ptr [rbx+150h]
    jmp qword ptr [followCameraReturnAddress]
FollowCameraCapture ENDP
end
