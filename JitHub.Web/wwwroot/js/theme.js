(() => {
    const storageKey = "jithub-web-theme";
    const root = document.documentElement;
    const systemThemeQuery = window.matchMedia("(prefers-color-scheme: dark)");
    const themeColor = document.querySelector("meta[data-theme-color]");
    const themeToggle = document.querySelector("[data-theme-toggle]");
    const themeToggleLabel = document.querySelector("[data-theme-toggle-label]");

    const getStoredTheme = () => {
        try {
            return localStorage.getItem(storageKey);
        } catch {
            return null;
        }
    };

    const setStoredTheme = (theme) => {
        try {
            localStorage.setItem(storageKey, theme);
        } catch {
        }
    };

    const getSystemTheme = () => systemThemeQuery.matches ? "dark" : "light";

    const normalizeTheme = (theme) => theme === "dark" || theme === "light" ? theme : getSystemTheme();

    const updateThemeImages = (theme) => {
        document.querySelectorAll("[data-theme-light-src][data-theme-dark-src]").forEach((image) => {
            const nextSource = theme === "dark" ? image.dataset.themeDarkSrc : image.dataset.themeLightSrc;
            if (nextSource && image.getAttribute("src") !== nextSource) {
                image.setAttribute("src", nextSource);
            }
        });
    };

    const applyTheme = (theme, persist) => {
        const normalizedTheme = normalizeTheme(theme);
        root.dataset.theme = normalizedTheme;
        root.style.colorScheme = normalizedTheme;

        if (themeColor) {
            themeColor.setAttribute("content", normalizedTheme === "dark" ? "#141a14" : "#eef1e7");
        }

        if (themeToggle) {
            const isDark = normalizedTheme === "dark";
            themeToggle.setAttribute("aria-pressed", String(isDark));
            themeToggle.setAttribute("aria-label", `Switch to ${isDark ? "light" : "dark"} theme`);
        }

        if (themeToggleLabel) {
            themeToggleLabel.textContent = normalizedTheme === "dark" ? "Dark" : "Light";
        }

        updateThemeImages(normalizedTheme);

        if (persist) {
            setStoredTheme(normalizedTheme);
        }
    };

    applyTheme(getStoredTheme(), false);

    themeToggle?.addEventListener("click", () => {
        applyTheme(root.dataset.theme === "dark" ? "light" : "dark", true);
    });

    systemThemeQuery.addEventListener("change", () => {
        if (!getStoredTheme()) {
            applyTheme(getSystemTheme(), false);
        }
    });
})();
