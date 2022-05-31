public asm_call_atkmodule_vf43_via_wndproc

.code

asm_call_atkmodule_vf43_via_wndproc proc
	ppfnCallWindowProcW:
		dq 0
	ppfnSetWindowLongPtrW:
		dq 0
	ppfnGetWindowLongPtrW:
		dq 0
	ppfnSetEvent:
		dq 0
	ppfnResetEvent:
		dq 0
	ppfnWaitForSingleObject:
		dq 0
	ppFrameworkInstance:
		dq 0
	ppfnFramework_GetUiModule:
		dq 0
	pGameHwnd:
		dq 0
	pEvent1:
		dq 0
	pEvent2:
		dq 0
	ppfnWndProcPrevious:
		dq 0

		dq 0CC90CC90CC90CC90h

	MyWndProc:
		sub rsp, 38h
		mov qword ptr [rsp + 28h], rcx
		
		mov qword ptr [rsp + 20h], r9
		mov r9, r8
		mov r8, rdx
		mov rdx, rcx
		mov rcx, qword ptr [ppfnWndProcPrevious]
		call qword ptr [ppfnCallWindowProcW]
		
		mov rcx, qword ptr [rsp + 28h]
		mov qword ptr [rsp + 28h], rax

		mov rdx, -4
		mov r8, qword ptr [ppfnWndProcPrevious]
		call qword ptr [ppfnSetWindowLongPtrW]

		mov rcx, qword ptr [pEvent1]
		call qword ptr [ppfnSetEvent]

		mov rcx, qword ptr [pEvent2]
		mov edx, 0FFFFFFFFh
		call qword ptr [ppfnWaitForSingleObject]
		
		mov rcx, qword ptr [ppFrameworkInstance]
		mov rcx, qword ptr [rcx]
		call qword ptr [ppfnFramework_GetUiModule]
		; rax = pUiModule
		
		mov rcx, rax
		mov rax, qword ptr [rax]
		add rax, 7 * 8
		mov rax, qword ptr [rax]
		call rax  ; vf7 GetRaptureAtkModule(this)
		; rax = pAtkModule
		
		mov rcx, rax
		mov rax, qword ptr [rax]
		add rax, 43 * 8
		mov rax, qword ptr [rax]
		xor rdx, rdx
		mov r8, 1
		call rax  ; vf43 GetFonts(this, bIsLobby, bForceReload)
		
		mov rax, qword ptr [rsp + 28h]
		add rsp, 38h
		ret

		dq 0CC90CC90CC90CC90h

	RedirectWndProc:
		sub rsp, 38h

		mov rcx, qword ptr [pGameHwnd]
		mov rdx, -4
		call qword ptr [ppfnGetWindowLongPtrW]
		mov qword ptr [ppfnWndProcPrevious], RAX
		mov rcx, qword ptr [pGameHwnd]
		mov rdx, -4
		lea r8, qword ptr [MyWndProc]
		call qword ptr [ppfnSetWindowLongPtrW]
		
		add rsp, 38h
		ret
		
		dq 0CC90CC90CC90CC90h
		dq 0CC90CC90CC90CC90h
asm_call_atkmodule_vf43_via_wndproc endp

end
