const authRoot = document.querySelector("[data-auth-root]");

if (authRoot) {
    const states = Array.from(authRoot.querySelectorAll("[data-auth-state]"));
    const errorText = authRoot.querySelector("[data-auth-error]");
    const launchLink = authRoot.querySelector("[data-auth-launch]");
    const handoffEndpoint = authRoot.getAttribute("data-handoff-endpoint") ?? "/api/GithubCodeToHandoff";
    const protocolV3StatePrefix = "WINUI3V3_";
    const debugProtocolV3StatePrefix = "WINUI3V3DEBUG_";

    const showState = (stateName) => {
        for (const state of states) {
            state.hidden = state.getAttribute("data-auth-state") !== stateName;
        }
    };

    const readErrorMessage = async (response) => {
        const fallback = `Token exchange failed with status ${response.status}.`;
        try {
            const contentType = response.headers.get("content-type") ?? "";
            if (contentType.includes("application/json")) {
                const payload = await response.json();
                return payload?.message || payload?.Message || fallback;
            }

            const text = (await response.text()).trim();
            return text || fallback;
        } catch {
            return fallback;
        }
    };

    const genericFailureMessage = "We could not complete sign-in. Please return to JitHub and start sign-in again.";

    const fail = (message) => {
        if (errorText && message) {
            errorText.textContent = message;
        }

        showState("failed");
    };

    const supportsProtocolV3 = (state) => state &&
        (state.startsWith(protocolV3StatePrefix) || state.startsWith(debugProtocolV3StatePrefix));

    const callbackScheme = (state) => state && state.startsWith(debugProtocolV3StatePrefix)
        ? "jithub-dev"
        : "jithub";

    const launchApp = async () => {
        const params = new URLSearchParams(window.location.search);
        const code = params.get("code");
        const state = params.get("state");

        if (!code || !state || !supportsProtocolV3(state)) {
            fail(genericFailureMessage);
            return;
        }

        try {
            const redirectUri = `${window.location.origin}${window.location.pathname}`;
            const response = await fetch(handoffEndpoint, {
                method: "POST",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                cache: "no-store",
                credentials: "same-origin",
                body: JSON.stringify({ tempCode: code, redirectUri, state })
            });

            if (!response.ok) {
                throw new Error(await readErrorMessage(response));
            }

            const payload = await response.json();
            const handoff = payload?.handoff ?? payload?.Handoff;
            if (!handoff) {
                throw new Error("The sign-in handoff was empty.");
            }

            const escapedHandoff = encodeURIComponent(handoff);
            const escapedState = encodeURIComponent(state);
            const scheme = callbackScheme(state);
            const protocolUri = `${scheme}://auth/v3?handoff=${escapedHandoff}&state=${escapedState}`;

            if (launchLink) {
                launchLink.setAttribute("href", protocolUri);
            }

            showState("success");

            window.setTimeout(() => {
                window.location.assign(protocolUri);
            }, 120);
        } catch (error) {
            fail(genericFailureMessage);
        }
    };

    showState("loading");
    void launchApp();
}
