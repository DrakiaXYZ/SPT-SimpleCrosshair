using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using DG.Tweening;
using EFT;
using EFT.UI;
using SimpleCrosshair.Config;
using SimpleCrosshair.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleCrosshair
{
    public enum EKeybindBehavior
    {
        DoNothing, PressToggles, ShowWhileHolding
    };

    public enum ECenterRadiusBehavior
    {
        DoNothing, HideInside, HideOutside
    };

    public class SimpleCrosshairComponent : MonoBehaviour
    {
        private readonly static string DefaultImagePath = Path.Combine(Plugin.Path, "crosshair.png");
        private readonly static HashSet<EPlayerState> PlayerStatesToHideWith = new HashSet<EPlayerState>()
        {
            EPlayerState.ProneMove,
            EPlayerState.Sprint,
            EPlayerState.BreachDoor,
        };
        private readonly static HashSet<Type> HandControllersToShowWith = new HashSet<Type>()
        {
            typeof(Player.FirearmController),
            typeof(Player.BaseKnifeController),
            typeof(Player.BaseGrenadeHandsController),
        };

        private Image _crosshairImage;
        private bool _visible = true;
        private bool _laggingVisible = true;
        private Dictionary<string, bool> _reasonsToHide = new Dictionary<string, bool>();

        // general config
        private Vector2 _offset;
        private float _fadeInOutTime;
        private Color _color;

        // dynamic positioning config
        private bool _useDynamicPosition;
        private float _dynamicPositionSmoothTime;
        private float _dynamicPositionAimDistance;
        private ECenterRadiusBehavior _centerRadiusBehavior;
        private float _centerRadius;

        // keybind config
        private KeyboardShortcut _keyboardShortcut;
        private EKeybindBehavior _keybindBehavior;

        // crosshair images
        private string _currentImage;
        private Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();

        // cached variables
        private Player _cachedPlayer;
        private Player.FirearmController _cachedFirearmController;
        private Canvas _cachedCanvas;
        private Camera _cachedCamera;

        public static GameObject AttachToBattleUIScreen(EftBattleUIScreen screen)
        {
            // setup container
            var go = new GameObject("SimpleCrosshair", typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = screen.gameObject.layer;
            go.transform.SetParent(screen.transform);
            go.transform.localScale = Vector3.one;
            go.GetRectTransform().anchoredPosition = Vector2.zero;
            go.GetRectTransform().pivot = new Vector2(0.5f, 0.5f);
            go.GetRectTransform().anchorMin = new Vector2(0.5f, 0.5f);
            go.GetRectTransform().anchorMax = new Vector2(0.5f, 0.5f);

            // add our component
            var component = go.AddComponent<SimpleCrosshairComponent>();
            return go;
        }

        private void Awake()
        {
            // create our image component that will hold the crosshair loaded from config
            _crosshairImage = gameObject.AddComponent<Image>();
            _crosshairImage.type = Image.Type.Simple;

            // we may have missed register player, so if a player exists, go ahead and call it
            // TODO: investigate if this is needed or if the patch should be used instead
            if (GameUtils.IsGameActive && GameUtils.Player != null)
            {
                OnRegisterMainPlayer();
            }

            ReadConfig();
        }

        private Sprite LoadSprite(string texturePath)
        {
            var texture = TextureUtils.LoadTexture2DFromPath(texturePath);
            var sprite = Sprite.Create(texture,
                                       new Rect(0f, 0f, texture.width, texture.height),
                                       new Vector2(texture.width / 2, texture.height / 2));
            return sprite;
        }

        public void Update()
        {
            // handle keyboard shortcuts
            if (_keybindBehavior == EKeybindBehavior.DoNothing || _keyboardShortcut.MainKey == KeyCode.None)
            {
                return;
            }

            switch (_keybindBehavior)
            {
                case EKeybindBehavior.PressToggles:
                    if (_keyboardShortcut.IsDown())
                    {
                        SetReasonToHide("disabled", !_reasonsToHide["disabled"]);
                    }
                    break;
                case EKeybindBehavior.ShowWhileHolding:
                    var isPressed = _keyboardShortcut.BetterIsPressed();
                    if (isPressed == _reasonsToHide["holdKeybind"])
                    {
                        SetReasonToHide("holdKeybind", !isPressed);
                    }
                    break;
                default:
                    break;
            }
        }

        public void FixedUpdate()
        {
            // handle dynamic position updates
            if (!_useDynamicPosition)
            {
                return;
            }

            CalculateDynamicAimPoint();
        }

        public void ReadConfig()
        {
            // general
            _color = Settings.Color.Value;
            _offset = new Vector2(Settings.OffsetX.Value, Settings.OffsetY.Value);
            _fadeInOutTime = Settings.FadeInOutTime.Value;
            gameObject.GetRectTransform().sizeDelta = Settings.Size.Value * Vector2.one;

            // reset color
            _crosshairImage.color = _color;

            // reset to static aim regardless, dynamic aim will update on fixed update
            ResetToStaticAimPoint();

            // dynamic positioning
            _useDynamicPosition = Settings.UseDynamicPosition.Value;
            _dynamicPositionSmoothTime = Settings.DynamicPositionSmoothTime.Value;
            _dynamicPositionAimDistance = Settings.DynamicPositionAimDistance.Value;
            _centerRadius = Settings.CenterRadius.Value;
            _centerRadiusBehavior = Settings.CenterRadiusBehavior.Value;

            // keybind shortcuts
            _keybindBehavior = Settings.KeybindBehavior.Value;
            _keyboardShortcut = Settings.KeyboardShortcut.Value;

            // load crosshair image from config if needed
            var imageName = Settings.ImageFileName.Value;
            var path = Path.Combine(Plugin.Path, imageName);

            // load sprite from path if not loaded before
            if (!_sprites.ContainsKey(path) && File.Exists(path))
            {
                _sprites[imageName] = LoadSprite(path);
            }

            // load sprite if not currently or if last loaded isn't this one
            if (_sprites.ContainsKey(imageName) && _currentImage != imageName)
            {
                _crosshairImage.sprite = _sprites[imageName];
                _currentImage = imageName;
            }

            // set initial reasons to hide and force visibility update
            _reasonsToHide["disabled"] = !Settings.Show.Value;
            _reasonsToHide["holdKeybind"] = _keybindBehavior == EKeybindBehavior.ShowWhileHolding;
            _reasonsToHide["centerRadius"] = false;
            SetVisibility(_visible, force: true);

            if (_useDynamicPosition)
            {
                CalculateDynamicAimPoint(false);
            }
        }

        public void SetReasonToHide(string reason, bool shouldHide)
        {
            _reasonsToHide[reason] = shouldHide;
            SetVisibility(!_reasonsToHide.Any((pair) => pair.Value));
        }

        private void SetVisibility(bool newVisible, bool shouldTween = true, bool force = false)
        {
            if (_visible == newVisible && !force)
            {
                return;
            }

            var toColor = new Color(_color.r, _color.g, _color.b, newVisible ? _color.a : 0);
            if (shouldTween)
            {
                // tween fade the crosshair in/out
                _crosshairImage.TweenColor(toColor, _fadeInOutTime).OnComplete(() => _laggingVisible = newVisible);
            }
            else
            {
                _crosshairImage.color = toColor;
                _laggingVisible = newVisible;
            }

            _visible = newVisible;
        }

        private void CalculateDynamicAimPoint(bool shouldTween = true)
        {
            if (_cachedPlayer == null)
            {
                return;
            }

            var rayBegin = _cachedFirearmController != null
                ? _cachedFirearmController.CurrentFireport.position
                : _cachedPlayer.CameraPosition.position;

            var rayDirection = _cachedFirearmController != null
                ? _cachedFirearmController.WeaponDirection
                : _cachedPlayer.LookDirection;

            // do raycast, if it hits, use that as aim point, if not, use end of ray
            var ray = new Ray(rayBegin, rayDirection);
            var rayEnd = rayBegin + rayDirection.normalized * _dynamicPositionAimDistance;
			var didRayHit = Physics.Raycast(
                ray, out RaycastHit rayHit, _dynamicPositionAimDistance, LayerMaskClass.HighPolyWithTerrainMask);

            var worldAimPoint = didRayHit ? rayHit.point : rayEnd;
            var screenAimPoint = GetCanvasScreenPosition(worldAimPoint);

            // move the anchor position of the cursor to the aim point
            // check visibility status before tweening so that if the player can't see it, it doesn't tween
            if (shouldTween && (_visible || _laggingVisible))
            {
                _crosshairImage.GetRectTransform().DOAnchorPos(screenAimPoint + _offset, _dynamicPositionSmoothTime);
            }
            else
            {
                _crosshairImage.GetRectTransform().anchoredPosition = screenAimPoint + _offset;
            }

            // lastly, do center radius handling
            if (_centerRadiusBehavior == ECenterRadiusBehavior.DoNothing)
            {
                return;
            }

            var shouldHide = false;
            var currentRadius = screenAimPoint.magnitude;
            if (_centerRadiusBehavior == ECenterRadiusBehavior.HideInside &&
                currentRadius < _centerRadius)
            {
                shouldHide = true;
            }
            else if(_centerRadiusBehavior == ECenterRadiusBehavior.HideOutside &&
                    currentRadius > _centerRadius)
            {
                shouldHide = true;
            }

            // only call if different than before, since we don't want to spam it, since called on fixed update
            if (shouldHide != _reasonsToHide["centerRadius"])
            {
                SetReasonToHide("centerRadius", shouldHide);
            }
        }

        private void ResetToStaticAimPoint()
        {
            DOTween.Kill(_crosshairImage.GetRectTransform());
            _crosshairImage.GetRectTransform().anchoredPosition = _offset;
        }

        private Vector2 GetCanvasScreenPosition(Vector3 worldPoint)
        {
            var canvasRect = _cachedCanvas.GetRectTransform().parent.GetRectTransform(); // Workaround to get a valid sizeDelta
            var viewportPoint = _cachedCamera.WorldToViewportPoint(worldPoint);
            return new Vector2(
                (viewportPoint.x * canvasRect.sizeDelta.x) - (canvasRect.sizeDelta.x * 0.5f),
                (viewportPoint.y * canvasRect.sizeDelta.y) - (canvasRect.sizeDelta.y * 0.5f));
        }

        internal void OnRegisterMainPlayer()
        {
            if (!GameUtils.IsGameActive) return;
            var player = GameUtils.Player;

            _cachedPlayer = player;
            _cachedCamera = Camera.main;
            _cachedCanvas = gameObject.GetComponentInParent<Canvas>();

            // register events on player
            player.OnHandsControllerChanged += OnHandControllerChanged;
            player.OnSenseChanged += OnSenseChanged;
            player.PossibleInteractionsChanged += PossibleInteractionsChanged;

            // we may have missed initial event, so call it now
            OnHandControllerChanged(null, player.HandsController);

            // clear all reasons to hide, since we're resetting the player
            _reasonsToHide.Clear();

            ReadConfig();
        }

        internal void OnUnregisterMainPlayer()
        {
            if (!GameUtils.IsGameActive) return;
            var player = GameUtils.Player;

            // unregister events on player
            player.OnHandsControllerChanged -= OnHandControllerChanged;
            player.OnSenseChanged -= OnSenseChanged;
            player.PossibleInteractionsChanged += PossibleInteractionsChanged;

            // unregister from hands controller if there is one
            OnHandControllerChanged(player.HandsController, null);
        }

        private void OnHandControllerChanged(Player.AbstractHandsController oldController,
                                             Player.AbstractHandsController newController)
        {
            if (oldController != null)
            {
                oldController.OnAimingChanged -= OnAimingChanged;
            }

            if (newController == null)
            {
                return;
            }

            newController.OnAimingChanged += OnAimingChanged;

            // check if controller is one that we want to show a crosshair for
            SetReasonToHide("handController", !HandControllersToShowWith.Any(c =>
                c.IsAssignableFrom(newController.GetType())));

            _cachedFirearmController = (newController is Player.FirearmController)
                ? newController as Player.FirearmController
                : null;
        }

        private void OnAimingChanged(bool isAiming)
        {
            SetReasonToHide("aiming", isAiming);
        }

        private void OnSenseChanged(bool sensingItem)
        {
            SetReasonToHide("sensing", sensingItem);
        }

        internal void OnMovementStateChanged(EPlayerState state)
        {
            SetReasonToHide("movementState", PlayerStatesToHideWith.Contains(state));
        }

        private void PossibleInteractionsChanged()
        {
            SetReasonToHide("interactableObject", GameUtils.Player.InteractableObject != null);
        }
    }
}
