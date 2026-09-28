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
    const getMediaSource = (image, theme) => theme === "dark" ? image.dataset.themeDarkSrc : image.dataset.themeLightSrc;
    const shouldLoadMedia = (image) => image.dataset.themeImmediate === "true" || image.dataset.mediaVisible === "true";

    const preloadImage = (source) => new Promise((resolve) => {
        if (!source) {
            resolve();
            return;
        }

        const preload = new Image();
        preload.onload = resolve;
        preload.onerror = resolve;
        preload.src = source;
    });

    const loadThemeImage = (image, theme = normalizeTheme(root.dataset.theme)) => {
        image.dataset.mediaVisible = "true";
        const source = getMediaSource(image, theme);
        if (source && image.getAttribute("src") !== source) {
            image.setAttribute("src", source);
        }
    };

    const updateThemeImages = async (theme, atomic) => {
        const images = Array.from(document.querySelectorAll("[data-theme-media]"))
            .filter(shouldLoadMedia);

        if (!atomic) {
            images.forEach((image) => loadThemeImage(image, theme));
            return;
        }

        await Promise.all(images.map((image) => preloadImage(getMediaSource(image, theme))));
        if (root.dataset.theme !== theme) {
            return;
        }

        images.forEach((image) => loadThemeImage(image, theme));
    };

    const applyTheme = (theme, persist, atomicMedia = false) => {
        const normalizedTheme = normalizeTheme(theme);
        root.dataset.theme = normalizedTheme;
        root.style.colorScheme = normalizedTheme;

        if (themeColor) {
            const browserThemeColor = getComputedStyle(root).getPropertyValue("--browser-theme-color").trim();
            if (browserThemeColor) {
                themeColor.setAttribute("content", browserThemeColor);
            }
        }

        if (themeToggle) {
            const isDark = normalizedTheme === "dark";
            themeToggle.setAttribute("aria-pressed", String(isDark));
            themeToggle.setAttribute("aria-label", `Switch to ${isDark ? "light" : "dark"} theme`);
        }

        if (themeToggleLabel) {
            themeToggleLabel.textContent = normalizedTheme === "dark" ? "Dark" : "Light";
        }

        void updateThemeImages(normalizedTheme, atomicMedia);

        if (persist) {
            setStoredTheme(normalizedTheme);
        }
    };

    window.JitHubThemeMedia = Object.freeze({
        load: loadThemeImage,
        currentTheme: () => normalizeTheme(root.dataset.theme)
    });

    applyTheme(getStoredTheme(), false);

    themeToggle?.addEventListener("click", () => {
        applyTheme(root.dataset.theme === "dark" ? "light" : "dark", true, true);
    });

    systemThemeQuery.addEventListener("change", () => {
        if (!getStoredTheme()) {
            applyTheme(getSystemTheme(), false, true);
        }
    });
})();
