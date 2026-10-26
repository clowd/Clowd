using System;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Clowd.UI.Controls.Tray
{
    /// <summary>
    /// The toggle half of a <see cref="TraySplitToggle"/> (spec §5): an ordinary <see cref="Button"/>
    /// whose one addition is an automation peer that answers the toggle pattern as well as the invoke
    /// pattern, so an assistive client learns the slot's on/off state (spec §2 "toggles expose pressed
    /// state", §10) instead of hearing a name and nothing else.
    /// <para>
    /// It stays a <see cref="Button"/> rather than becoming a <c>ToggleButton</c> because the slot
    /// <b>never flips itself</b> — see <see cref="TraySplitToggle"/>'s remarks. The peer therefore only
    /// <i>reports</i> a state it reads back off the slot and <i>requests</i> a change by raising exactly
    /// the click a pointer would; who decides what that click means is still the owner's business.
    /// </para>
    /// <para>
    /// No <c>ControlThemes.EnsureRegistered()</c> in a static constructor, unlike the other controls in
    /// this folder: this type ships no theme of its own. It wears the two themes declared in
    /// TraySplitToggle.axaml, which the slot's template hands it explicitly.
    /// </para>
    /// </summary>
    public sealed class TrayToggleHalfButton : Button
    {
        private TrayToggleHalfAutomationPeer _peer;

        /// <summary>
        /// Styled as a plain <see cref="Button"/>, and this is load-bearing rather than tidy: Avalonia
        /// matches a type selector against <c>StyleKey</c>, so the slot theme's
        /// <c>Button#PART_Toggle</c> rules — padding, height, the per-orientation corner radii — and the
        /// <c>TargetType="Button"</c> of the half's own <c>ControlTheme</c> would all stop matching the
        /// moment this became a subclass with its own style key, and the half would lose its shape.
        /// </summary>
        protected override Type StyleKeyOverride => typeof(Button);

        protected override AutomationPeer OnCreateAutomationPeer() =>
            _peer = new TrayToggleHalfAutomationPeer(this);

        /// <summary>
        /// Tells a listening assistive client that the slot's state moved. Called by the owning
        /// <see cref="TraySplitToggle"/> when its <see cref="TraySplitToggle.IsOn"/> changes; a bool that
        /// raised a change notification always came from the other value, so no old value is passed in.
        /// Does nothing until something has actually asked for a peer.
        /// </summary>
        public void NotifyToggleStateChanged(bool isOn)
        {
            _peer?.RaiseToggleStateChanged(isOn);
        }

        /// <summary>
        /// A button peer that also implements <see cref="IToggleProvider"/>. The state lives on the
        /// templated parent, so the peer reads it through <c>TemplatedParent</c> rather than holding a
        /// copy that could go stale.
        /// </summary>
        public sealed class TrayToggleHalfAutomationPeer : ButtonAutomationPeer, IToggleProvider
        {
            public TrayToggleHalfAutomationPeer(TrayToggleHalfButton owner)
                : base(owner)
            {
            }

            public ToggleState ToggleState =>
                Owner.TemplatedParent is TraySplitToggle slot && slot.IsOn
                    ? ToggleState.On
                    : ToggleState.Off;

            /// <summary>
            /// Asks for a flip the same way a click asks for one — and deliberately does not write
            /// <see cref="TraySplitToggle.IsOn"/>: the owner may repaint at once, may open a menu
            /// instead, or may be a read-only light, and a peer that wrote the state would be lying in
            /// two of those three cases.
            /// </summary>
            public void Toggle() => Invoke();

            /// <summary>
            /// Pinned: spec §10 asks for these halves to keep reading as buttons with a state, not to
            /// turn into check boxes because a toggle pattern appeared on them.
            /// </summary>
            protected override AutomationControlType GetAutomationControlTypeCore() =>
                AutomationControlType.Button;

            public void RaiseToggleStateChanged(bool isOn) =>
                RaisePropertyChangedEvent(
                    TogglePatternIdentifiers.ToggleStateProperty,
                    isOn ? ToggleState.Off : ToggleState.On,
                    isOn ? ToggleState.On : ToggleState.Off);
        }
    }
}
