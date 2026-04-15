const authRoot = document.querySelector("[data-auth-root]");

if (authRoot) {
    const states = Array.from(authRoot.querySelectorAll("[data-auth-state]"));
    const errorText = authRoot.querySelector("[data-auth-error]");
    const launchLink = authRoot.querySelector("[data-auth-launch]");
    const tokenEndpoint = authRoot.getAttribute("data-token-endpoint") ?? "/api/GithubCodeToToken";
    const protocolV2StatePrefix = "WINUI3V2_";

    const showState = (stateName) => {
        for (const state of states) {
            state.hidden = state.getAttribute("data-auth-state") !== stateName;
        }
    };

    const fail = (message) => {
        if (errorText && message) {
            errorText.textContent = message;
        }

        showState("failed");
    };

    const supportsProtocolV2 = (state) => state && state.startsWith(protocolV2StatePrefix);

    const launchApp = async () => {
        const params = new URLSearchParams(window.location.search);
        const code = params.get("code");
        const state = params.get("state");

        if (!code || !state) {
            fail("The GitHub callback is missing the temporary code or state value.");
            return;
        }

        try {
            const response = await fetch(`${tokenEndpoint}?tempCode=${encodeURIComponent(code)}`, {
                headers: {
                    "Accept": "text/plain"
                }
            });

            if (!response.ok) {
                throw new Error(`Token exchange failed with status ${response.status}.`);
            }

            const token = (await response.text()).trim();
            if (!token) {
                throw new Error("GitHub returned an empty access token.");
            }

            const escapedToken = encodeURIComponent(token);
            const escapedState = encodeURIComponent(state);
            const protocolUri = supportsProtocolV2(state)
                ? `jithub://auth/v2?token=${escapedToken}&state=${escapedState}`
                : `jithub://auth/?token=${escapedToken}&state=${escapedState}#state=${escapedState}`;

            if (launchLink) {
                launchLink.setAttribute("href", protocolUri);
            }

            showState("success");

            window.setTimeout(() => {
                window.location.assign(protocolUri);
            }, 120);
        } catch (error) {
            console.error("Failed to complete JitHub authorization.", error);
            fail("GitHub approved the request, but the site could not complete the handoff back to JitHub.");
        }
    };

    showState("loading");
    void launchApp();
}
