option casemap:none

PUBLIC CameraPositionCapture

extern cameraPositionReturnAddress : QWORD
extern cameraPositionZeroTarget    : QWORD
extern capturedCameraPosition      : QWORD
extern freezeCameraPosition        : BYTE

.code
CameraPositionCapture PROC
    mov qword ptr [capturedCameraPosition], rdi
    ; Match CE startfreecam exactly: capture rdi (rbx+A0), then suppress only
    ; this position write while freecam owns the native camera. The remainder
    ; of the camera function still consumes rdi and builds downstream state.
    cmp byte ptr [freezeCameraPosition], 1
    je skip_position_write
    movaps xmmword ptr [rdi], xmm6
skip_position_write:
    cmp byte ptr [rbx+315h], 0
    je zero_path
    xorps xmm1, xmm1
    jmp qword ptr [cameraPositionReturnAddress]
zero_path:
    jmp qword ptr [cameraPositionZeroTarget]
CameraPositionCapture ENDP
end
