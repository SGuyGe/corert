// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
#include "ICodeManager.h"

struct ExInfo;
typedef DPTR(ExInfo) PTR_ExInfo;
enum ExKind : UInt8
{
    EK_HardwareFault = 2,
    EK_SuperscededFlag  = 8,
};

struct EHEnum
{
    ICodeManager * m_pCodeManager;
    EHEnumState m_state;
};

EXTERN_C Boolean FASTCALL RhpSfiInit(StackFrameIterator* pThis, PAL_LIMITED_CONTEXT* pStackwalkCtx);
EXTERN_C Boolean FASTCALL RhpSfiNext(StackFrameIterator* pThis, UInt32* puExCollideClauseIdx, Boolean* pfUnwoundReversePInvoke);

struct PInvokeTransitionFrame;
typedef DPTR(PInvokeTransitionFrame) PTR_PInvokeTransitionFrame;
typedef DPTR(PAL_LIMITED_CONTEXT) PTR_PAL_LIMITED_CONTEXT;

class StackFrameIterator
{
    friend class AsmOffsets;
    friend Boolean FASTCALL RhpSfiInit(StackFrameIterator* pThis, PAL_LIMITED_CONTEXT* pStackwalkCtx);
    friend Boolean FASTCALL RhpSfiNext(StackFrameIterator* pThis, UInt32* puExCollideClauseIdx, Boolean* pfUnwoundReversePInvoke);

public:
    StackFrameIterator() {}
    StackFrameIterator(Thread * pThreadToWalk, PTR_VOID pInitialTransitionFrame);
    StackFrameIterator(Thread * pThreadToWalk, PTR_PAL_LIMITED_CONTEXT pCtx);


    bool            IsValid();
    void            CalculateCurrentMethodState();
    void            Next();
    UInt32          GetCodeOffset();
    REGDISPLAY *    GetRegisterSet();
    ICodeManager *  GetCodeManager();
    MethodInfo *    GetMethodInfo();
    bool            GetHijackedReturnValueLocation(PTR_RtuObjectRef * pLocation, GCRefKind * pKind);

    static bool     IsValidReturnAddress(PTR_VOID pvAddress);

    // Support for conservatively reporting GC references in a stack range. This is used when managed methods
    // with an unknown signature potentially including GC references call into the runtime and we need to let
    // a GC proceed (typically because we call out into managed code again). Instead of storing signature
    // metadata for every possible managed method that might make such a call we identify a small range of the
    // stack that might contain outgoing arguments. We then report every pointer that looks like it might
    // refer to the GC heap as a fixed interior reference.
    bool HasStackRangeToReportConservatively();
    void GetStackRangeToReportConservatively(PTR_RtuObjectRef * ppLowerBound, PTR_RtuObjectRef * ppUpperBound);

private:
    // If our control PC indicates that we're in one of the thunks we use to make managed callouts from the
    // runtime we need to adjust the frame state to that of the managed method that previously called into the
    // runtime (i.e. skip the intervening unmanaged frames).
    // NOTE: This function always publishes a non-NULL conservative stack range lower bound.
    void UnwindManagedCalloutThunk();

    // The invoke of a funclet is a bit special and requires an assembly thunk, but we don't want to break the
    // stackwalk due to this.  So this routine will unwind through the assembly thunks used to invoke funclets.
    // It's also used to disambiguate exceptionally- and non-exceptionally-invoked funclets.
    void UnwindFuncletInvokeThunk();
    void UnwindThrowSiteThunk();

    // If our control PC indicates that we're in the universal transition thunk that we use to generically
    // dispatch arbitrary managed calls, then handle the stack walk specially.
    // NOTE: This function always publishes a non-NULL conservative stack range lower bound.
    void UnwindUniversalTransitionThunk();

    // If our control PC indicates that we're in the call descr thunk that we use to call an arbitrary managed
    // function with an arbitrary signature from a normal managed function handle the stack walk specially.
    void UnwindCallDescrThunk();

    void EnterInitialInvalidState(Thread * pThreadToWalk);

    void InternalInit(Thread * pThreadToWalk, PTR_PInvokeTransitionFrame pFrame, UInt32 dwFlags); // GC stackwalk
    void InternalInit(Thread * pThreadToWalk, PTR_PAL_LIMITED_CONTEXT pCtx, UInt32 dwFlags);  // EH and hijack stackwalk, and collided unwind
    void InternalInitForEH(Thread * pThreadToWalk, PAL_LIMITED_CONTEXT * pCtx);             // EH stackwalk
    void InternalInitForStackTrace();  // Environment.StackTrace

    PTR_VOID HandleExCollide(PTR_ExInfo pExInfo);
    void NextInternal();

    // This will walk m_pNextExInfo from its current value until it finds the next ExInfo at a higher address
    // than the SP reference value passed in.  This is useful when 'restarting' the stackwalk from a 
    // particular PInvokeTransitionFrame or after we have a 'collided unwind' that may skip over ExInfos. 
    void ResetNextExInfoForSP(UIntNative SP);

    void UpdateStateForRemappedGCSafePoint(UInt32 funcletStartOffset);
    void RemapHardwareFaultToGCSafePoint();

    void UpdateFromExceptionDispatch(PTR_StackFrameIterator pSourceIterator);

    // helpers to ApplyReturnAddressAdjustment
    PTR_VOID AdjustReturnAddressForward(PTR_VOID controlPC);
    PTR_VOID AdjustReturnAddressBackward(PTR_VOID controlPC);

    void UnwindNonEHThunkSequence();
    void PrepareToYieldFrame();

    enum ReturnAddressCategory
    {
        InManagedCode,
        InThrowSiteThunk,
        InFuncletInvokeThunk,
        InManagedCalloutThunk,
        InCallDescrThunk,
        InUniversalTransitionThunk,
    };

    static ReturnAddressCategory CategorizeUnadjustedReturnAddress(PTR_VOID returnAddress);
    static bool IsNonEHThunk(ReturnAddressCategory category);

    enum Flags
    {
        // If this flag is set, each unwind will apply a -1 to the ControlPC.  This is used by EH to ensure 
        // that the ControlPC of a callsite stays within the containing try region.
        ApplyReturnAddressAdjustment = 1,

        // Used by the GC stackwalk, this flag will ensure that multiple funclet frames for a given method
        // activation will be given only one callback.  The one callback is given for the most nested physical
        // stack frame of a given activation of a method.  (i.e. the leafmost funclet)
        CollapseFunclets             = 2,

        // This is a state returned by Next() which indicates that we just crossed an ExInfo in our unwind.
        ExCollide                    = 4,

        // If a hardware fault frame is encountered, report its control PC at the binder-inserted GC safe 
        // point immediately after the prolog of the most nested enclosing try-region's handler.
        RemapHardwareFaultsToSafePoint = 8,

        MethodStateCalculated = 0x10,

        // This is a state returned by Next() which indicates that we just unwound a reverse pinvoke method
        UnwoundReversePInvoke = 0x20,

        GcStackWalkFlags = (CollapseFunclets | RemapHardwareFaultsToSafePoint),
        EHStackWalkFlags = ApplyReturnAddressAdjustment,
        StackTraceStackWalkFlags = GcStackWalkFlags
    };

    struct PreservedRegPtrs
    {
#ifdef _TARGET_ARM_
        PTR_UIntNative pR4;
        PTR_UIntNative pR5;
        PTR_UIntNative pR6;
        PTR_UIntNative pR7;
        PTR_UIntNative pR8;
        PTR_UIntNative pR9;
        PTR_UIntNative pR10;
        PTR_UIntNative pR11;
#elif defined(UNIX_AMD64_ABI)
        PTR_UIntNative pRbp;
        PTR_UIntNative pRbx;
        PTR_UIntNative pR12;
        PTR_UIntNative pR13;
        PTR_UIntNative pR14;
        PTR_UIntNative pR15;
#else // _TARGET_ARM_
        PTR_UIntNative pRbp;
        PTR_UIntNative pRdi;
        PTR_UIntNative pRsi;
        PTR_UIntNative pRbx;
#ifdef _TARGET_AMD64_
        PTR_UIntNative pR12;
        PTR_UIntNative pR13;
        PTR_UIntNative pR14;
        PTR_UIntNative pR15;
#endif // _TARGET_AMD64_
#endif // _TARGET_ARM_
    };

protected:
    Thread *            m_pThread;
    RuntimeInstance *   m_pInstance;
    PTR_VOID            m_FramePointer;
    PTR_VOID            m_ControlPC;
    REGDISPLAY          m_RegDisplay;
    ICodeManager *      m_pCodeManager;
    MethodInfo          m_methodInfo;
    UInt32              m_codeOffset;
    PTR_RtuObjectRef    m_pHijackedReturnValue;
    GCRefKind           m_HijackedReturnValueKind;
    PTR_UIntNative      m_pConservativeStackRangeLowerBound;
    PTR_UIntNative      m_pConservativeStackRangeUpperBound;
    UInt32              m_dwFlags;
    PTR_ExInfo          m_pNextExInfo;
    PTR_VOID            m_pendingFuncletFramePointer;
    PreservedRegPtrs    m_funcletPtrs;  // @TODO: Placing the 'scratch space' in the StackFrameIterator is not
                                        // preferred because not all StackFrameIterators require this storage 
                                        // space.  However, the implementation simpler by doing it this way.
};

